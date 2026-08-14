using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Ghostty.Tests.Interop;

// Pins that every P/Invoke surface honors [assembly: DisableRuntimeMarshalling]
// (no [MarshalAs], two-BOOL-shape: byte for libghostty _Bool, int for Win32 BOOL).
//
// Two scopes, because the rules are not the same everywhere. The libghostty
// boundary in NativeMethods.cs bans [MarshalAs] outright: that surface is
// hand-written against a C header and its shape is the convention.
//
// The struct rules apply to every [StructLayout] struct in the corpus, which
// is every source under windows/Ghostty and windows/Ghostty.Core that
// declares a native import or an interop struct. A non-blittable field there
// is not a style question: runtime marshalling is off, so field marshalling
// is not honored, and the mistake surfaces as a failed call rather than a
// build error. [StructLayout] is what scopes it -- it is how this codebase
// marks a struct that crosses the boundary, so a managed struct sitting in
// the same file cannot be flagged for a rule that does not apply to it.
//
// The corpus comes from a wildcard in Ghostty.Tests.csproj, so a new interop
// file is scanned without anyone remembering to add it here. It is the two
// project directories rather than the two assemblies: windows/Ghostty/Demo
// is compiled only into demo builds but is scanned always, which costs
// nothing since it is real code in the Debug binary.
public class MarshalComplianceTests
{
    private const string ResourceName = "Ghostty.Tests.Interop.Imports.NativeMethods.cs";

    // Every C# source of the two DisableRuntimeMarshalling projects.
    // Covers both the wildcard corpus under Sources\ and the two files
    // embedded separately under Imports\ for the entry-point parity test:
    // the same file cannot be embedded twice, and the .zig exports under
    // this prefix are filtered out by extension below.
    private const string SourcePrefix = "Ghostty.Tests.Interop.";

    // Note: `StringMarshalling = StringMarshalling.Utf8` is a separate,
    // supported mechanism and is NOT what this test scans for. Only the
    // `[MarshalAs` attribute form is a compliance violation.
    private const string BannedAttribute = "[MarshalAs";

    // Two subsequences of UnmanagedType we care about. These are the
    // values the spec explicitly calls out as the audit targets.
    private static readonly string[] BannedUnmanagedTypes = new[]
    {
        "UnmanagedType.Bool",
        "UnmanagedType.I1",
    };

    // A file is interop if it declares a native import or an interop
    // struct. [DllImport] is in here as well as [LibraryImport] so the
    // older form cannot slip a surface past the scan by being the thing
    // nobody looks for, and the layout attributes because this codebase
    // keeps several ABI structs in files that import nothing themselves
    // (Ghostty.Core/Interop/*.cs) -- exactly where a non-blittable field
    // would be introduced.
    // What marks a struct as one the runtime hands to native code.
    // [InlineArray] is here alongside [StructLayout] because an inline
    // array is a layout even without one: KeybindInterop's TriggerSteps
    // carries no [StructLayout] and is a by-value field of a struct that
    // a P/Invoke returns.
    private static readonly string[] LayoutAttributes = new[]
    {
        "[StructLayout",
        "[InlineArray",
    };

    private static readonly string[] InteropMarkers =
        new[] { "[LibraryImport", "[DllImport" }.Concat(LayoutAttributes).ToArray();

    // `struct Name`, in any of the modifier orders C# allows. `record
    // struct` is excluded: it is the managed idiom in this codebase (handle
    // wrappers and value tuples), never a layout the runtime hands to
    // native code.
    private static readonly Regex StructDeclaration = new(
        @"(?<!\brecord\s)\bstruct\s+[A-Za-z_]\w*",
        RegexOptions.Compiled);

    // A field or auto-property of a type runtime marshalling would have had
    // to convert. Leading attributes are allowed, so a field sharing a line
    // with [MarshalAs] is still seen, and the access modifier is optional,
    // since a field without one is private and still part of the layout.
    // `char*` does not match: the pointer is what makes it blittable.
    private const string FieldModifiers =
        @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|readonly|volatile|required|new|unsafe)\s+)*";
    private const string NonBlittableTypes =
        @"(?<type>(?:System\.)?(?:bool|Boolean|char|Char|string|String))(?:\?|\[\])?";

    // `= value` is allowed (a struct field initializer), `=> expr` is not:
    // an expression-bodied property is computed, not stored.
    private static readonly Regex NonBlittableField = new(
        FieldModifiers + NonBlittableTypes + @"\s+\w+(?:\s*,\s*\w+)*\s*(?:=(?!>)[^;]*)?;\s*$",
        RegexOptions.Compiled);

    // An auto-property has a compiler-generated backing field, so it is
    // part of the layout even though it does not look like a field.
    private static readonly Regex NonBlittableAutoProperty = new(
        FieldModifiers + NonBlittableTypes + @"\s+\w+\s*\{\s*get\s*;",
        RegexOptions.Compiled);

    // Neither rule applies to these: a `fixed` buffer is blittable by
    // construction, and const/static members are not part of the layout.
    private static readonly Regex NotLayoutMember = new(
        @"\b(?:fixed|const|static)\b",
        RegexOptions.Compiled);

    [Fact]
    public void NativeMethods_HasNoMarshalAsAttributes()
    {
        var source = ReadEmbeddedSource();
        var offending = ScanForBannedAttribute(source, BannedAttribute);

        Assert.True(
            offending.Count == 0,
            "NativeMethods.cs must not contain [MarshalAs] attributes under " +
            "[assembly: DisableRuntimeMarshalling]. Offending lines:\n" +
            string.Join("\n", offending.Select(l => $"  line {l.Number}: {l.Text.Trim()}")));
    }

    [Fact]
    public void NativeMethods_HasNoUnmanagedTypeBoolOrI1()
    {
        var source = ReadEmbeddedSource();
        var offending = ScanForBannedTokens(source, BannedUnmanagedTypes);

        Assert.True(
            offending.Count == 0,
            "NativeMethods.cs must not reference UnmanagedType.Bool or " +
            "UnmanagedType.I1. Two-BOOL-shape convention: byte for " +
            "libghostty C99 _Bool, int for Win32 BOOL. Offending lines:\n" +
            string.Join("\n", offending.Select(l => $"  line {l.Number}: {l.Text.Trim()}")));
    }

    // The corpus guard. Without it a wildcard that stopped matching, or a
    // marker that stopped being recognized, would leave every scan below
    // passing over nothing. Each half of the corpus needs its own anchor:
    // NativeMethods.cs is hand-listed in the csproj, so it proves nothing
    // about the wildcards, and two anchors from the same project would not
    // notice the other project's glob going dead.
    [Fact]
    public void InteropCorpus_CoversBothProjects()
    {
        var scanned = InteropSources().Select(s => s.Name).ToList();

        Assert.NotEmpty(scanned);
        Assert.Contains(scanned, n => n.EndsWith("NativeMethods.cs", StringComparison.Ordinal));

        // From the windows\Ghostty wildcard.
        Assert.Contains(scanned, n => n.EndsWith("SplashWindow.cs", StringComparison.Ordinal));
        // From the windows\Ghostty.Core wildcard.
        Assert.Contains(scanned, n => n.EndsWith("NtProcessInterop.cs", StringComparison.Ordinal));
        // The ABI structs that declare no import of their own.
        Assert.Contains(scanned, n => n.EndsWith("GhosttySharedTexture.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void InteropSources_HaveNoMarshalAsOnStructFields()
    {
        var offending = new List<string>();
        foreach (var (name, source) in InteropSources())
        {
            offending.AddRange(
                ScanStructFieldsForBannedAttribute(source)
                    .Select(l => $"  {name} line {l.Number}: {l.Text.Trim()}"));
        }

        Assert.True(
            offending.Count == 0,
            "[MarshalAs] on an interop struct field does nothing under " +
            "[assembly: DisableRuntimeMarshalling]: the struct is passed as " +
            "laid out, so the field has to be blittable already. Offending " +
            "lines:\n" + string.Join("\n", offending));
    }

    [Fact]
    public void InteropSources_HaveNoNonBlittableStructFields()
    {
        var offending = new List<string>();
        foreach (var (name, source) in InteropSources())
        {
            offending.AddRange(
                ScanStructFieldsForNonBlittableTypes(source)
                    .Select(l => $"  {name} line {l.Number}: {l.Text.Trim()}"));
        }

        Assert.True(
            offending.Count == 0,
            "bool, char and string are not blittable, so an interop layout " +
            "struct carrying one cannot cross the boundary with runtime " +
            "marshalling disabled. Use byte for a C99 _Bool, int for a Win32 " +
            "BOOL, and a char* or IntPtr for a string. Offending lines:\n" +
            string.Join("\n", offending));
    }

    // Unit test: comment-only mention must not false-positive.
    [Fact]
    public void Scanner_IgnoresMarshalAsInsideLineComment()
    {
        const string sample =
            "public partial class Fake\n" +
            "{\n" +
            "    // [MarshalAs(UnmanagedType.I1)] explanation kept for historical context\n" +
            "    public byte Composing;\n" +
            "}\n";

        var attrHits = ScanForBannedAttribute(sample, BannedAttribute);
        var tokenHits = ScanForBannedTokens(sample, BannedUnmanagedTypes);

        Assert.Empty(attrHits);
        Assert.Empty(tokenHits);
    }

    // Unit test: a real attribute line must be flagged.
    [Fact]
    public void Scanner_FlagsRealMarshalAsAttributeLine()
    {
        const string sample =
            "public partial struct Fake\n" +
            "{\n" +
            "    [MarshalAs(UnmanagedType.I1)]\n" +
            "    public bool Composing;\n" +
            "}\n";

        var attrHits = ScanForBannedAttribute(sample, BannedAttribute);
        var tokenHits = ScanForBannedTokens(sample, BannedUnmanagedTypes);

        Assert.Single(attrHits);
        Assert.Single(tokenHits);
    }

    // Unit test: the CsWin32 boundary allowlist skips lines that
    // reference a Windows.Win32.* type, but still flags lines that
    // carry a [MarshalAs] with no CsWin32 marker. This pins the
    // behavior of IsCsWin32Boundary so a future edit that loosens or
    // tightens the rule trips this test.
    [Fact]
    public void Scanner_AllowsCsWin32BoundaryButFlagsBareMarshalAs()
    {
        const string sample =
            "public partial struct Fake\n" +
            "{\n" +
            "    [MarshalAs(UnmanagedType.Bool)] public Windows.Win32.Foundation.BOOL Allowed;\n" +
            "    [MarshalAs(UnmanagedType.Bool)] public bool Flagged;\n" +
            "}\n";

        var attrHits = ScanForBannedAttribute(sample, BannedAttribute);
        var tokenHits = ScanForBannedTokens(sample, BannedUnmanagedTypes);

        // Exactly one violation: the bare-bool line. The CsWin32
        // boundary line is allowlisted.
        Assert.Single(attrHits);
        Assert.Contains("Flagged", attrHits[0].Text);
        Assert.Single(tokenHits);
        Assert.Contains("Flagged", tokenHits[0].Text);
    }

    // Unit test: the struct rules must see every shape that ends up in the
    // layout, and nothing else. A [return: MarshalAs] on an import, an
    // expression-bodied property, a char pointer and a fixed buffer are all
    // legal and must stay quiet; a field sharing its line with an attribute
    // and an auto-property must not.
    [Fact]
    public void StructScanner_SeparatesLayoutMembersFromEverythingElse()
    {
        const string sample =
            "internal static partial class Fake\n" +
            "{\n" +
            "    [LibraryImport(\"user32.dll\")]\n" +
            "    [return: MarshalAs(UnmanagedType.Bool)]\n" +
            "    private static partial bool IsWindow(nint hwnd);\n" +
            "\n" +
            "    [StructLayout(LayoutKind.Sequential)]\n" +
            "    private unsafe struct Blittable\n" +
            "    {\n" +
            "        public byte Composing;\n" +
            "        public char* Name;\n" +
            "        public fixed char Buffer[8];\n" +
            "        public const string Sentinel = \"x\";\n" +
            "        public bool IsComposing => Composing != 0;\n" +
            "    }\n" +
            "\n" +
            "    [StructLayout(LayoutKind.Sequential)]\n" +
            "    private struct Broken\n" +
            "    {\n" +
            "        [MarshalAs(UnmanagedType.I1)]\n" +
            "        public bool Enabled;\n" +
            "        [FieldOffset(4)] public bool Packed;\n" +
            "        public string Name { get; set; }\n" +
            "        bool NoModifier;\n" +
            "    }\n" +
            "\n" +
            "    [StructLayout(LayoutKind.Sequential)]\n" +
            "    private struct OneLiner { public string Label; }\n" +
            "}\n";

        var attrHits = ScanStructFieldsForBannedAttribute(sample);
        var fieldHits = ScanStructFieldsForNonBlittableTypes(sample);

        // Only the [MarshalAs]: [FieldOffset] is layout, not marshalling,
        // and the [return:] one belongs to the import above the structs.
        Assert.Single(attrHits);
        Assert.Contains("UnmanagedType.I1", attrHits[0].Text);

        Assert.Equal(5, fieldHits.Count);
        foreach (var expected in new[] { "Enabled", "Packed", "Name { get;", "NoModifier", "Label" })
        {
            Assert.Contains(fieldHits, l => l.Text.Contains(expected, StringComparison.Ordinal));
        }
    }

    // Unit test: a struct with no [StructLayout] is managed, and the rules
    // are about what crosses the boundary. Flagging it would fail the build
    // over a string field that never reaches native code.
    [Fact]
    public void StructScanner_IgnoresStructsThatAreNotInteropLayouts()
    {
        const string sample =
            "internal static partial class Fake\n" +
            "{\n" +
            "    [LibraryImport(\"user32.dll\")]\n" +
            "    private static partial int GetSystemMetrics(int index);\n" +
            "\n" +
            "    private struct Managed\n" +
            "    {\n" +
            "        public string Path;\n" +
            "    }\n" +
            "\n" +
            "    private readonly record struct Handle(string Name)\n" +
            "    {\n" +
            "        public string Label;\n" +
            "    }\n" +
            "}\n";

        Assert.Empty(ScanStructFieldsForNonBlittableTypes(sample));
    }

    // Unit test: a local inside a method the struct declares is not part
    // of the layout, and a member whose body starts on the next line is
    // still a property. Both would otherwise fail the build over code that
    // never crosses the boundary.
    [Fact]
    public void StructScanner_IgnoresLocalsAndMultiLineProperties()
    {
        const string sample =
            "[StructLayout(LayoutKind.Sequential)]\n" +
            "internal struct Handle\n" +
            "{\n" +
            "    public IntPtr Ptr;\n" +
            "\n" +
            "    public bool IsEmpty\n" +
            "        => Ptr == IntPtr.Zero;\n" +
            "\n" +
            "    public string Describe()\n" +
            "    {\n" +
            "        string text = Ptr.ToString();\n" +
            "        bool empty = text.Length == 0;\n" +
            "        return empty ? string.Empty : text;\n" +
            "    }\n" +
            "}\n";

        Assert.Empty(ScanStructFieldsForNonBlittableTypes(sample));
    }

    // Unit test: braces inside literals and block comments must not move
    // the struct bookkeeping. A dropped brace closes a struct early and
    // silences the rest of it; an extra one leaves it open and flags plain
    // managed fields further down the file.
    [Fact]
    public void StructScanner_IgnoresBracesInsideLiteralsAndBlockComments()
    {
        const string sample =
            "[StructLayout(LayoutKind.Sequential)]\n" +
            "internal struct Config\n" +
            "{\n" +
            "    public byte Ready;\n" +
            "    /* struct Old { public bool Legacy; */\n" +
            "    public const string Close = \"}\";\n" +
            "    public char Brace;\n" +
            "    public string Trailing;\n" +
            "}\n" +
            "\n" +
            "internal sealed class After\n" +
            "{\n" +
            "    public bool Shown;\n" +
            "}\n";

        var hits = ScanStructFieldsForNonBlittableTypes(sample);

        // The two real fields inside the struct, and nothing from the class
        // that follows it.
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, l => l.Text.Contains("Brace", StringComparison.Ordinal));
        Assert.Contains(hits, l => l.Text.Contains("Trailing", StringComparison.Ordinal));
    }

    // Allowlist: lines referencing Windows.Win32.* (CsWin32 boundary, marshalling
    // already enforced upstream).
    private static bool IsCsWin32Boundary(string codeLine)
        => codeLine.Contains("Windows.Win32.", StringComparison.Ordinal);

    private static List<Line> ScanForBannedAttribute(string source, string banned)
    {
        var hits = new List<Line>();
        foreach (var line in EnumerateLines(source))
        {
            if (IsCsWin32Boundary(line.Code)) continue;
            if (line.Code.Contains(banned, StringComparison.Ordinal))
            {
                hits.Add(line);
            }
        }
        return hits;
    }

    private static List<Line> ScanForBannedTokens(string source, string[] bannedTokens)
    {
        var hits = new List<Line>();
        foreach (var line in EnumerateLines(source))
        {
            if (IsCsWin32Boundary(line.Code)) continue;
            if (bannedTokens.Any(bt => line.Code.Contains(bt, StringComparison.Ordinal)))
            {
                hits.Add(line);
            }
        }
        return hits;
    }

    private static List<Line> ScanStructFieldsForBannedAttribute(string source)
    {
        var hits = new List<Line>();
        foreach (var line in EnumerateLines(source))
        {
            if (!line.InInteropStruct) continue;
            if (IsCsWin32Boundary(line.Code)) continue;
            // A [return:] attribute belongs to an import declared beside
            // the fields, not to the layout.
            if (line.Code.Contains("[return:", StringComparison.Ordinal)) continue;
            if (line.Code.Contains(BannedAttribute, StringComparison.Ordinal))
            {
                hits.Add(line);
            }
        }
        return hits;
    }

    private static List<Line> ScanStructFieldsForNonBlittableTypes(string source)
    {
        var hits = new List<Line>();
        foreach (var line in EnumerateLines(source))
        {
            if (!line.InInteropStruct) continue;
            if (IsCsWin32Boundary(line.Code)) continue;
            // A declaration that ends on a later line: `public bool X` with
            // its `=> expr` or `{ get; }` below it. Judging it here would
            // read a property as a field.
            if (!line.Code.Contains(';') && !line.Code.Contains('{')) continue;

            foreach (var member in SplitMembers(line.Code))
            {
                if (NotLayoutMember.IsMatch(member)) continue;
                if (NonBlittableField.IsMatch(member) || NonBlittableAutoProperty.IsMatch(member))
                {
                    hits.Add(line);
                    break;
                }
            }
        }
        return hits;
    }

    /// <summary>
    /// The declarations on one line of a struct body. Usually one, but a
    /// one-liner struct carries its declaration and every field on the same
    /// line, and the fields there are still layout.
    /// </summary>
    private static IEnumerable<string> SplitMembers(string codeLine)
    {
        var body = codeLine;
        var brace = body.IndexOf('{');
        if (brace >= 0 && StructDeclaration.IsMatch(body)) body = body[(brace + 1)..];

        foreach (var part in body.Split(';'))
        {
            // Split drops the terminator the field patterns anchor on, and
            // the auto-property pattern needs the `{ get;` that split ate.
            yield return part + ";";
        }
    }

    private static string ReadEmbeddedSource()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Every embedded source that declares a native import or an interop
    /// struct, by resource name and content.
    /// </summary>
    private static List<(string Name, string Source)> InteropSources()
    {
        var asm = Assembly.GetExecutingAssembly();
        var sources = new List<(string, string)>();

        foreach (var name in asm.GetManifestResourceNames()
                     .Where(n => n.StartsWith(SourcePrefix, StringComparison.Ordinal)
                         && n.EndsWith(".cs", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var source = reader.ReadToEnd();
            if (InteropMarkers.Any(m => source.Contains(m, StringComparison.Ordinal)))
            {
                sources.Add((name[SourcePrefix.Length..], source));
            }
        }

        return sources;
    }

    private readonly record struct Line(int Number, string Text, string Code, bool InInteropStruct);

    /// <summary>
    /// The source's lines, each paired with its code (comments and literal
    /// contents removed) and whether it sits inside a <c>[StructLayout]</c>
    /// struct body. Brace counting rather than parsing: enough to tell a
    /// layout member from a method local, which is the only distinction the
    /// rules above need. Raw string literals are the one gap: the
    /// delimiter line is dropped whole rather than guessed at, but the
    /// body between delimiters is still read as code.
    /// </summary>
    private static IEnumerable<Line> EnumerateLines(string source)
    {
        var lines = source.Split('\n');
        var depth = 0;
        // Open struct bodies, each with the brace depth it opened at and
        // whether it is an interop layout.
        var structs = new Stack<(int Depth, bool Layout)>();
        var pendingStruct = false;
        var pendingLayout = false;
        var sawStructLayout = false;
        var inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var code = StripCommentsAndLiterals(lines[i], ref inBlockComment);
            if (LayoutAttributes.Any(a => code.Contains(a, StringComparison.Ordinal)))
            {
                sawStructLayout = true;
            }

            var opensStruct = StructDeclaration.IsMatch(code);

            // Only the struct's own body, not deeper: a member sits at the
            // depth the opening brace reached, while anything inside a
            // method the struct declares is one deeper and is a local, not
            // layout. A one-liner declares the struct and its members on
            // the same line, so the line that opens a body counts too.
            var insideLayout = structs.Count > 0
                ? structs.Peek().Layout && structs.Peek().Depth == depth
                : opensStruct && sawStructLayout && code.Contains('{');

            yield return new Line(i + 1, lines[i], code, insideLayout);

            if (opensStruct)
            {
                pendingStruct = true;
                pendingLayout = sawStructLayout;
            }

            foreach (var c in code)
            {
                if (c == '{')
                {
                    depth++;
                    if (pendingStruct)
                    {
                        structs.Push((depth, pendingLayout));
                        pendingStruct = false;
                    }
                }
                else if (c == '}')
                {
                    if (structs.Count > 0 && structs.Peek().Depth == depth) structs.Pop();
                    depth--;
                }
                else if (c == ';')
                {
                    // A bodyless declaration: `record struct Point(int X);`.
                    // Without this the pending open would latch onto the
                    // next unrelated brace in the file. It also ends the
                    // reach of a [StructLayout] that decorated something
                    // other than a struct.
                    pendingStruct = false;
                    sawStructLayout = false;
                }
            }

            // An attribute applies to the next declaration, so it survives
            // blank lines and further attributes and nothing else.
            if (opensStruct || (code.Contains('{') && !pendingStruct)) sawStructLayout = false;
        }
    }

    /// <summary>
    /// One line with its comments and its string and char literal contents
    /// removed, so that a brace or a keyword inside either cannot move the
    /// scanner's bookkeeping.
    /// </summary>
    private static string StripCommentsAndLiterals(string rawLine, ref bool inBlockComment)
    {
        // Raw string literals can span lines and carry anything at all.
        // Nothing in this codebase declares interop inside one, so drop the
        // line rather than pretend to parse it.
        if (!inBlockComment && rawLine.Contains("\"\"\"", StringComparison.Ordinal)) return string.Empty;

        var code = new StringBuilder(rawLine.Length);
        var i = 0;
        while (i < rawLine.Length)
        {
            if (inBlockComment)
            {
                if (rawLine[i] == '*' && i + 1 < rawLine.Length && rawLine[i + 1] == '/')
                {
                    inBlockComment = false;
                    i += 2;
                }
                else i++;
                continue;
            }

            var c = rawLine[i];
            if (c == '/' && i + 1 < rawLine.Length && rawLine[i + 1] == '/') break;
            if (c == '/' && i + 1 < rawLine.Length && rawLine[i + 1] == '*')
            {
                inBlockComment = true;
                i += 2;
                continue;
            }

            if (c == '"')
            {
                // @", $@" and @$" are all verbatim; the interpolation
                // marker can sit on either side of the @.
                var verbatim = (i > 0 && rawLine[i - 1] == '@')
                    || (i > 1 && rawLine[i - 2] == '@');
                i = SkipStringLiteral(rawLine, i, verbatim);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(rawLine, i);
                continue;
            }

            code.Append(c);
            i++;
        }

        return code.ToString();
    }

    /// <summary>Index just past the closing quote, or end of line if unterminated.</summary>
    private static int SkipStringLiteral(string line, int openIndex, bool verbatim)
    {
        var i = openIndex + 1;
        while (i < line.Length)
        {
            if (verbatim)
            {
                // "" is an escaped quote in a verbatim string.
                if (line[i] == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { i += 2; continue; }
                    return i + 1;
                }
                i++;
                continue;
            }

            if (line[i] == '\\') { i += 2; continue; }
            if (line[i] == '"') return i + 1;
            i++;
        }
        return i;
    }

    private static int SkipCharLiteral(string line, int openIndex)
    {
        var i = openIndex + 1;
        while (i < line.Length)
        {
            if (line[i] == '\\') { i += 2; continue; }
            if (line[i] == '\'') return i + 1;
            i++;
        }
        return i;
    }
}
