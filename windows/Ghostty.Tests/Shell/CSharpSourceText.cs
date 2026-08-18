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
/// looking for that API.
///
/// Literals are replaced by an empty literal of the same kind rather than
/// deleted, so the surrounding code keeps its shape and offsets stay ordered.
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

            // Verbatim, in any of its prefix spellings: @"", $@"", @$"".
            var verbatim = VerbatimBodyStart(source, i);
            if (verbatim > 0)
            {
                i = SkipVerbatim(source, verbatim);
                sb.Append("\"\"");
                continue;
            }

            if (c == '"' || (c == '$' && Next(source, i) == '"'))
            {
                i = SkipRegular(source, c == '$' ? i + 2 : i + 1);
                sb.Append("\"\"");
                continue;
            }

            if (c == '\'')
            {
                i = SkipRegular(source, i + 1, terminator: '\'');
                sb.Append("''");
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

    // Returns the index just past the opening quote of a verbatim literal
    // starting at i, or 0 when there is not one there.
    private static int VerbatimBodyStart(string s, int i)
    {
        if (s[i] == '@' && Next(s, i) == '"') return i + 2;
        if (s[i] == '@' && Next(s, i) == '$' && i + 2 < s.Length && s[i + 2] == '"') return i + 3;
        if (s[i] == '$' && Next(s, i) == '@' && i + 2 < s.Length && s[i + 2] == '"') return i + 3;
        return 0;
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
}
