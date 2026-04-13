using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Interop;

// Guardrail for PR 203's DisableRuntimeMarshalling convention. The
// Ghostty assembly carries [assembly: DisableRuntimeMarshalling], and
// the two-BOOL-shape convention (byte for libghostty C99 _Bool, int
// for Win32 BOOL) means no [LibraryImport] or [UnmanagedFunctionPointer]
// signature in NativeMethods.cs should carry a [MarshalAs(...)] hint.
//
// The Ghostty project is a WinAppSDK project; this test project is
// plain net9.0 and cannot reference it. NativeMethods.cs is embedded
// as a manifest resource via <EmbeddedResource Link=...> in
// Ghostty.Tests.csproj so this test can read it without a project
// reference.
//
// Scanner rule: strip trailing `//` line comments before scanning, so
// that explanatory comments like `// [MarshalAs] was removed here`
// do NOT false-positive. The scanner does NOT strip `/* ... */` block
// comments; the project convention in NativeMethods.cs is line
// comments only, and adding a block-comment stripper would add state
// to the scanner without a matching benefit.
//
// When this test fails: either the convention is being reintroduced
// (fix the signature), or the convention intentionally moved (update
// this test).
public class MarshalComplianceTests
{
    private const string ResourceName = "Ghostty.Tests.Interop.NativeMethods.cs";

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

    // Strip trailing `//` line comments from each source line before
    // searching, so commentary like `// [MarshalAs] was removed here`
    // does not trigger a false positive. The cheapest correct
    // implementation: look for the first `//` and take the prefix.
    // This is NOT a full C# tokenizer: it does not understand string
    // literals containing `//`, but NativeMethods.cs has no such
    // lines today and adding one would be obviously wrong anyway.
    //
    // PR 202 allowlist: lines that reference a Windows.Win32.* type
    // are CsWin32-generated boundaries where BOOL is the strongly-
    // typed Windows.Win32.Foundation.BOOL struct (4 bytes, implicit
    // bool conversion). The two BOOL conventions coexist by scope:
    // hand-written [LibraryImport] uses int + != 0 (PR 203), CsWin32
    // call sites use BOOL via implicit conversion. Today this scanner
    // only reads NativeMethods.cs (libghostty surface, no CsWin32),
    // so the rule is a future-proofing measure for if/when the scan
    // scope ever widens to other interop files.
    private static bool IsCsWin32Boundary(string strippedLine)
        => strippedLine.Contains("Windows.Win32.", StringComparison.Ordinal);

    private static List<(int Number, string Text)> ScanForBannedAttribute(string source, string banned)
    {
        var hits = new List<(int Number, string Text)>();
        foreach (var pair in EnumerateLines(source))
        {
            var stripped = StripLineComment(pair.Text);
            if (IsCsWin32Boundary(stripped)) continue;
            if (stripped.Contains(banned, StringComparison.Ordinal))
            {
                hits.Add(pair);
            }
        }
        return hits;
    }

    private static List<(int Number, string Text)> ScanForBannedTokens(string source, string[] bannedTokens)
    {
        var hits = new List<(int Number, string Text)>();
        foreach (var pair in EnumerateLines(source))
        {
            var stripped = StripLineComment(pair.Text);
            if (IsCsWin32Boundary(stripped)) continue;
            if (bannedTokens.Any(bt => stripped.Contains(bt, StringComparison.Ordinal)))
            {
                hits.Add(pair);
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

    private static IEnumerable<(int Number, string Text)> EnumerateLines(string source)
    {
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            yield return (i + 1, lines[i]);
        }
    }
}
