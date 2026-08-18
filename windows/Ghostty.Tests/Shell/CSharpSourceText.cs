using System;
using System.Text;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Reduces C# source to the part a wiring assertion may look at: comments and
/// string/char literal CONTENT removed, everything else intact.
///
/// Both halves matter and both were learned the hard way. A comment above a
/// statement can quote that statement verbatim -- the ordering comments in
/// App.xaml.cs do exactly that -- so a raw-text search finds the prose and
/// passes while the code says the opposite. A string literal can do the same:
/// a diagnostic message naming the API it reports on satisfies any assertion
/// looking for that API. An interpolation hole is code inside a literal, so a
/// nested literal inside one has to be skipped as a literal in its own right,
/// or its contents leak back into the text an assertion reads.
///
/// Literals are replaced by an empty literal rather than deleted, so the
/// surrounding code keeps its shape and offsets stay ordered.
///
/// KNOWN LIMIT: raw string literals are not parsed. Their content can hold an
/// unbalanced number of quotes, which would desynchronise this scanner and
/// make it swallow real code silently -- the worst failure a helper that
/// fourteen assertions depend on could have. One is therefore refused loudly
/// via <see cref="NotSupportedException"/>. There is not one in the scanned
/// corpus today; a source file that grows one gets a diagnostic naming this
/// class, not a quiet false green.
/// </summary>
internal static class CSharpSourceText
{
    public static string Strip(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && Next(source, i) == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (c == '/' && Next(source, i) == '*')
            {
                i += 2;
                while (i < source.Length && !(source[i] == '*' && Next(source, i) == '/')) i++;
                i = Math.Min(i + 2, source.Length);
                continue;
            }

            if (c == '\'')
            {
                i = SkipRegular(source, i + 1, terminator: '\'');
                sb.Append("''");
                continue;
            }

            if (TryReadLiteral(source, i, out var end))
            {
                sb.Append("\"\"");
                i = end;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// The body of the member declared by <paramref name="declaration"/>,
    /// stripped. Scoping keeps an assertion about one method from passing on a
    /// match somewhere else in a 1700-line file.
    /// </summary>
    public static string Member(string source, string declaration)
    {
        var stripped = Strip(source);

        var start = stripped.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' is gone from the source");

        var open = stripped.IndexOf('{', start);
        Assert.True(open >= 0, $"no body found for '{declaration}'");

        var depth = 0;
        for (var i = open; i < stripped.Length; i++)
        {
            if (stripped[i] == '{') depth++;
            else if (stripped[i] == '}')
            {
                depth--;
                if (depth == 0) return stripped[start..(i + 1)];
            }
        }

        Assert.Fail($"unbalanced braces reading the body of '{declaration}'");
        return string.Empty;
    }

    /// <summary>
    /// Index of <paramref name="needle"/>, asserting it is present. Callers
    /// compare these to assert order, which is the only thing this style of
    /// test can meaningfully say.
    /// </summary>
    public static int RequireIndex(string body, string needle, string whatIsMissing)
    {
        var at = body.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, whatIsMissing);
        return at;
    }

    public static int Count(string body, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = body.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    private static char Next(string s, int i) => i + 1 < s.Length ? s[i + 1] : '\0';

    /// <summary>
    /// If a string literal starts at <paramref name="i"/> -- including its
    /// <c>@</c> / <c>$</c> prefixes in either order -- reports the index just
    /// past its closing quote.
    /// </summary>
    private static bool TryReadLiteral(string s, int i, out int end)
    {
        end = 0;

        var j = i;
        var verbatim = false;
        var interpolated = false;
        while (j < s.Length && (s[j] == '@' || s[j] == '$'))
        {
            if (s[j] == '@') verbatim = true; else interpolated = true;
            j++;
        }

        // A prefix character only IS a prefix when a quote follows it;
        // otherwise it was an identifier sigil and this is not a literal.
        if (j >= s.Length || s[j] != '"') return false;

        var quotes = 0;
        while (j + quotes < s.Length && s[j + quotes] == '"') quotes++;

        // Three or more opening quotes is a raw string -- unless the literal is
        // verbatim, where a run just means it opens with escaped quotes.
        if (!verbatim && quotes >= 3)
        {
            throw new NotSupportedException(
                "CSharpSourceText cannot read raw string literals. One has appeared in a "
                + "scanned source file. Parsing it wrongly would silently delete code from "
                + "the text the wiring assertions read, so it is refused instead. Teach this "
                + "class the raw-string forms before scanning that file.");
        }

        // An empty regular literal: nothing to skip past.
        if (!verbatim && quotes == 2)
        {
            end = j + 2;
            return true;
        }

        end = interpolated
            ? SkipInterpolated(s, j + 1, verbatim)
            : verbatim ? SkipVerbatim(s, j + 1) : SkipRegular(s, j + 1);
        return true;
    }

    // In a verbatim literal a doubled quote is an escaped quote; a lone one
    // ends it. Backslash has no special meaning.
    private static int SkipVerbatim(string s, int i)
    {
        while (i < s.Length)
        {
            if (s[i] != '"') { i++; continue; }
            if (Next(s, i) == '"') { i += 2; continue; }
            return i + 1;
        }

        return i;
    }

    private static int SkipRegular(string s, int i, char terminator = '"')
    {
        while (i < s.Length)
        {
            if (s[i] == '\\') { i += 2; continue; }
            if (s[i] == terminator) return i + 1;
            i++;
        }

        return i;
    }

    // An interpolated literal is text with holes of real code in it. Only a
    // quote at brace depth zero closes it; inside a hole a quote opens a
    // nested literal, which has to be skipped as one or its contents surface
    // in the stripped text and satisfy assertions they have nothing to do with.
    private static int SkipInterpolated(string s, int i, bool verbatim)
    {
        var depth = 0;

        while (i < s.Length)
        {
            var c = s[i];

            if (!verbatim && c == '\\' && depth == 0) { i += 2; continue; }

            if (c == '{')
            {
                if (Next(s, i) == '{' && depth == 0) { i += 2; continue; }
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (depth == 0)
                {
                    if (Next(s, i) == '}') { i += 2; continue; }
                    i++;
                    continue;
                }

                depth--;
                i++;
                continue;
            }

            if (depth > 0)
            {
                if (TryReadLiteral(s, i, out var nested)) { i = nested; continue; }
                if (c == '\'') { i = SkipRegular(s, i + 1, terminator: '\''); continue; }
                i++;
                continue;
            }

            if (c == '"')
            {
                if (verbatim && Next(s, i) == '"') { i += 2; continue; }
                return i + 1;
            }

            i++;
        }

        return i;
    }
}
