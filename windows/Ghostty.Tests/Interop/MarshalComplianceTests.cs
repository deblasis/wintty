using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Ghostty.Tests.Interop;

// Pins that every P/Invoke surface honors [assembly: DisableRuntimeMarshalling]
// (no [MarshalAs], two-BOOL-shape: byte for libghostty _Bool, int for Win32 BOOL).
//
// Two scopes, because the rules are not the same everywhere. The libghostty
// boundary in NativeMethods.cs bans [MarshalAs] outright: that surface is
// hand-written against a C header and its shape is the convention. The
// struct rules below apply to every source in the two assemblies that set
// DisableRuntimeMarshalling, because a non-blittable interop struct is not a
// style question -- runtime marshalling is off, so field marshalling is not
// honored, and the mistake surfaces as a failed call rather than a build
// error. That corpus comes from a wildcard in Ghostty.Tests.csproj, so a new
// interop file is scanned without anyone remembering to add it here.
public class MarshalComplianceTests
{
    private const string ResourceName = "Ghostty.Tests.Interop.Imports.NativeMethods.cs";

    // Every C# source of the two DisableRuntimeMarshalling assemblies.
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

    // A file is interop if it declares a native import. [DllImport] is in
    // here as well as [LibraryImport] so the older form cannot slip a
    // surface past the scan by being the thing nobody looks for.
    private static readonly string[] InteropMarkers = new[]
    {
        "[LibraryImport",
        "[DllImport",
    };

    // `struct Name`, in any of the modifier orders C# allows. Only the
    // keyword and the name are matched; the modifiers in front vary and
    // none of them change whether a body follows.
    private static readonly Regex StructDeclaration = new(
        @"\bstruct\s+[A-Za-z_]\w*",
        RegexOptions.Compiled);

    // A plain field of a type runtime marshalling would have had to
    // convert. Deliberately narrow: an access modifier, the type, one or
    // more names, a semicolon, end of line. Anything with `=>` or a call
    // in it is a property or a local, which are not part of the struct's
    // memory layout and are fine. `char*` does not match, because the
    // pointer is what makes it blittable.
    private static readonly Regex NonBlittableField = new(
        @"^\s*(?:public|internal|private|protected)\s+(?:readonly\s+|volatile\s+)*"
            + @"(?<type>bool|char|string)\??\s+\w+(?:\s*,\s*\w+)*\s*;\s*$",
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
    // passing over nothing.
    [Fact]
    public void InteropCorpus_CoversTheHandWrittenSurfaces()
    {
        var scanned = InteropSources().Select(s => s.Name).ToList();

        Assert.NotEmpty(scanned);
        Assert.Contains(scanned, n => n.EndsWith("NativeMethods.cs", StringComparison.Ordinal));
        Assert.Contains(scanned, n => n.EndsWith("SplashWindow.cs", StringComparison.Ordinal));
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
            "bool, char and string are not blittable, so a struct carrying " +
            "one cannot cross the boundary with runtime marshalling " +
            "disabled. Use byte for a C99 _Bool, int for a Win32 BOOL, and " +
            "a char* or IntPtr for a string. Offending lines:\n" +
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

    // Unit test: the struct-scoped rules must see struct bodies and
    // nothing else. A [return: MarshalAs] on an import, and a bool local
    // or expression-bodied property, are all legal and must stay quiet.
    [Fact]
    public void StructScanner_SeparatesStructFieldsFromEverythingElse()
    {
        const string sample =
            "internal static partial class Fake\n" +
            "{\n" +
            "    [LibraryImport(\"user32.dll\")]\n" +
            "    [return: MarshalAs(UnmanagedType.Bool)]\n" +
            "    private static partial bool IsWindow(nint hwnd);\n" +
            "\n" +
            "    private struct Blittable\n" +
            "    {\n" +
            "        public byte Composing;\n" +
            "        public char* Name;\n" +
            "        public bool IsComposing => Composing != 0;\n" +
            "    }\n" +
            "\n" +
            "    private struct Broken\n" +
            "    {\n" +
            "        [MarshalAs(UnmanagedType.I1)]\n" +
            "        public bool Enabled;\n" +
            "    }\n" +
            "\n" +
            "    private struct OneLiner { public string Label; }\n" +
            "}\n";

        var attrHits = ScanStructFieldsForBannedAttribute(sample);
        var fieldHits = ScanStructFieldsForNonBlittableTypes(sample);

        Assert.Single(attrHits);
        Assert.Contains("UnmanagedType.I1", attrHits[0].Text);

        Assert.Equal(2, fieldHits.Count);
        Assert.Contains(fieldHits, l => l.Text.Contains("Enabled", StringComparison.Ordinal));
        Assert.Contains(fieldHits, l => l.Text.Contains("Label", StringComparison.Ordinal));
    }

    // Allowlist: lines referencing Windows.Win32.* (CsWin32 boundary, marshalling
    // already enforced upstream).
    private static bool IsCsWin32Boundary(string strippedLine)
        => strippedLine.Contains("Windows.Win32.", StringComparison.Ordinal);

    private static List<Line> ScanForBannedAttribute(string source, string banned)
    {
        var hits = new List<Line>();
        foreach (var line in EnumerateLines(source))
        {
            var stripped = StripLineComment(line.Text);
            if (IsCsWin32Boundary(stripped)) continue;
            if (stripped.Contains(banned, StringComparison.Ordinal))
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
            var stripped = StripLineComment(line.Text);
            if (IsCsWin32Boundary(stripped)) continue;
            if (bannedTokens.Any(bt => stripped.Contains(bt, StringComparison.Ordinal)))
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
            if (!line.InStruct) continue;
            var stripped = StripLineComment(line.Text);
            if (IsCsWin32Boundary(stripped)) continue;
            // A [return:] attribute belongs to an import declared beside
            // the fields, not to the layout.
            if (stripped.Contains("[return:", StringComparison.Ordinal)) continue;
            if (stripped.Contains(BannedAttribute, StringComparison.Ordinal))
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
            if (!line.InStruct) continue;
            var stripped = StripLineComment(line.Text);
            if (IsCsWin32Boundary(stripped)) continue;
            // One-liner struct bodies carry the declaration and the fields
            // on the same line, so scan the body rather than the whole line.
            var body = stripped;
            var brace = body.IndexOf('{');
            if (brace >= 0 && StructDeclaration.IsMatch(body)) body = body[(brace + 1)..];

            foreach (var field in body.Split(';'))
            {
                if (NonBlittableField.IsMatch(field + ";"))
                {
                    hits.Add(line);
                    break;
                }
            }
        }
        return hits;
    }

    private static string StripLineComment(string rawLine)
    {
        var commentIdx = rawLine.IndexOf("//", StringComparison.Ordinal);
        return commentIdx >= 0 ? rawLine.Substring(0, commentIdx) : rawLine;
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
    /// Every embedded source that declares a native import, by resource
    /// name and content.
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

    private readonly record struct Line(int Number, string Text, bool InStruct);

    /// <summary>
    /// The source's lines, each tagged with whether it sits inside a
    /// struct body. Brace counting rather than parsing: enough to tell a
    /// layout field from a method local, which is the only distinction
    /// the rules above need.
    /// </summary>
    private static IEnumerable<Line> EnumerateLines(string source)
    {
        var lines = source.Split('\n');
        var depth = 0;
        var structDepths = new Stack<int>();
        var pendingStruct = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var stripped = StripLineComment(lines[i]);
            var opensStruct = StructDeclaration.IsMatch(stripped);

            // A one-liner declares the struct and its fields on the same
            // line, so the line that opens a body counts as inside it.
            yield return new Line(
                i + 1,
                lines[i],
                structDepths.Count > 0 || (opensStruct && stripped.Contains('{')));

            if (opensStruct) pendingStruct = true;
            foreach (var c in stripped)
            {
                if (c == '{')
                {
                    depth++;
                    if (pendingStruct)
                    {
                        structDepths.Push(depth);
                        pendingStruct = false;
                    }
                }
                else if (c == '}')
                {
                    if (structDepths.Count > 0 && structDepths.Peek() == depth)
                    {
                        structDepths.Pop();
                    }
                    depth--;
                }
                else if (c == ';')
                {
                    // A bodyless declaration: `record struct Point(int X);`.
                    // Without this the pending open would latch onto the
                    // next unrelated brace in the file.
                    pendingStruct = false;
                }
            }
        }
    }
}
