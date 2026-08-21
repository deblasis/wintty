using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ghostty.Core.Cli;
using Xunit;

namespace Ghostty.Tests.Cli;

// Pins the console shim's alias table to CliAliases.Actions.
//
// dist/windows/cli-shim decides whether the calling shell waits for
// Wintty.exe, and it has to decide before the app runs, so it carries its
// own copy of the subcommand list. A third copy of a table that already
// rots (see CliAliasParityTests) needs its own guard: when the two drift,
// `wintty <alias>` returns to the prompt and prints over it, and
// `wintty validate-config && deploy` proceeds regardless of the result,
// because the shim's non-waiting branch always exits 0.
//
// Same technique as CliAliasParityTests: the zig source is embedded by
// Ghostty.Tests.csproj rather than read from disk, so the test does not
// depend on where the assembly runs from.
public class CliShimParityTests
{
    private const string ShimResource = "Ghostty.Tests.Cli.Shim.main.zig";

    // The shim brackets its table with these so this test finds the array
    // and not, say, a doc comment listing the same words.
    private const string TableOpen = "// wintty:aliases:begin";
    private const string TableClose = "// wintty:aliases:end";

    private static readonly Regex EntryPattern = new(
        @"^\s*""(?<name>[^""]+)"",\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void ShimAliasTableMatchesCliAliases()
    {
        var shim = ReadShimSource();
        var declared = ParseAliasTable(shim);

        Assert.True(declared.Count > 0,
            $"No alias entries found between {TableOpen} and {TableClose}. If the "
            + "shim's table moved or the markers were removed, this test cannot "
            + "pin anything and must be repaired rather than deleted.");

        var expected = CliAliases.Actions.OrderBy(a => a, StringComparer.Ordinal).ToList();
        var actual = declared.OrderBy(a => a, StringComparer.Ordinal).ToList();

        var missing = expected.Except(actual, StringComparer.Ordinal).ToList();
        var extra = actual.Except(expected, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            "dist/windows/cli-shim/main.zig's alias table has drifted from "
            + "CliAliases.Actions.\n"
            + $"  missing from the shim: {Fmt(missing)}\n"
            + $"  present only in the shim: {Fmt(extra)}\n"
            + "A subcommand missing from the shim means the shell stops waiting "
            + "for it: its output lands after the prompt and its exit code is "
            + "reported as 0.");
    }

    // The two version spellings the shim special-cases are the ones
    // Program.MainImpl intercepts on top of the alias table. If that
    // interception grows a spelling, the shim needs it too.
    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void ShimCoversTheVersionSpellingsProgramIntercepts(string spelling)
    {
        var shim = ReadShimSource();
        Assert.Contains($"\"{spelling}\"", shim, StringComparison.Ordinal);
    }

    private static string Fmt(IReadOnlyCollection<string> items)
        => items.Count == 0 ? "(none)" : string.Join(", ", items);

    private static List<string> ParseAliasTable(string source)
    {
        var open = source.IndexOf(TableOpen, StringComparison.Ordinal);
        var close = source.IndexOf(TableClose, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open,
            $"Could not locate {TableOpen}/{TableClose} in the shim source.");

        var body = source[(open + TableOpen.Length)..close];
        return EntryPattern.Matches(body)
            .Select(m => m.Groups["name"].Value)
            .ToList();
    }

    private static string ReadShimSource()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ShimResource);
        Assert.True(stream is not null,
            $"Embedded resource {ShimResource} not found. Check the "
            + "EmbeddedResource entry for dist/windows/cli-shim/main.zig in "
            + "Ghostty.Tests.csproj.");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
