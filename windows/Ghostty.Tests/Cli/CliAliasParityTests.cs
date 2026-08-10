using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ghostty.Core.Cli;
using Xunit;

namespace Ghostty.Tests.Cli;

// Pins CliAliases.Actions to the Action enum in src/cli/ghostty.zig.
//
// The Windows subcommand aliases are upstream divergence, and the alias
// table is the part that rots: upstream adds an action, nobody notices,
// and `wintty <new-action>` silently opens a terminal window instead of
// running it. This fails the build instead.
//
// Same technique as EntryPointParityTests, which pins [LibraryImport]
// entry points against zig `export fn` sites. The zig source is embedded
// by Ghostty.Tests.csproj rather than read from disk, so the test does not
// depend on where the assembly runs from.
public class CliAliasParityTests
{
    private const string ActionResource = "Ghostty.Tests.Cli.Actions.ghostty.zig";

    // `pub const Action = enum {` opens the block; the first four-space
    // `pub fn` (detectSpecialCase) closes it. Slicing rather than scanning
    // the whole file keeps runMain's `.@"list-themes" =>` switch prongs,
    // which look exactly like field declarations, out of the corpus.
    private const string EnumOpen = "pub const Action = enum {";
    private static readonly Regex DeclStart = new(
        @"^    pub fn ", RegexOptions.Compiled | RegexOptions.Multiline);

    // Both spellings zig permits for these names: bare `ssh,` and quoted
    // `@"list-themes",`. Anchored to end of line so nothing longer can
    // masquerade as a field.
    private static readonly Regex FieldPattern = new(
        @"^\s*(?:@""(?<name>[^""]+)""|(?<name>[A-Za-z_][A-Za-z0-9_]*)),\s*$",
        RegexOptions.Compiled);

    // Lines that legitimately appear between fields.
    private static readonly Regex IgnorablePattern = new(
        @"^\s*(?://.*)?$", RegexOptions.Compiled);

    [Fact]
    public void EveryLibghosttyActionHasAnAlias()
    {
        var declared = ReadActionFields();

        Assert.NotEmpty(declared);

        var missing = declared.Except(CliAliases.Actions, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var stale = CliAliases.Actions.Except(declared, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        if (missing.Count > 0 || stale.Count > 0)
        {
            Assert.Fail(
                "CliAliases.Actions is out of sync with the Action enum in " +
                "src/cli/ghostty.zig.\n" +
                (missing.Count > 0
                    ? "Actions libghostty exposes with no Windows alias (add them " +
                      "to CliAliases.Actions):\n" +
                      string.Join("\n", missing.Select(n => $"  {n}")) + "\n"
                    : string.Empty) +
                (stale.Count > 0
                    ? "Aliases with no matching action (upstream removed them; " +
                      "delete them from CliAliases.Actions):\n" +
                      string.Join("\n", stale.Select(n => $"  {n}"))
                    : string.Empty));
        }
    }

    // Without this, an upstream action declared in a syntax FieldPattern
    // does not match would be absent from BOTH sets, set equality would
    // hold, and a missing alias would ship on a green build.
    [Fact]
    public void EveryLineInTheEnumBlockIsAccountedFor()
    {
        var unrecognized = EnumBlockLines()
            .Where(line => !FieldPattern.IsMatch(line) && !IgnorablePattern.IsMatch(line))
            .ToList();

        if (unrecognized.Count > 0)
        {
            Assert.Fail(
                "Unrecognized lines inside the Action enum block in " +
                "src/cli/ghostty.zig. The field regex in CliAliasParityTests no " +
                "longer describes how upstream declares actions, so the parity " +
                "check above can no longer see them:\n" +
                string.Join("\n", unrecognized.Select(l => $"  {l}")));
        }
    }

    // Guards the csproj resource entry. Losing it would make both tests
    // above scan an empty string.
    [Fact]
    public void ActionSourceIsEmbedded()
    {
        Assert.Contains(
            ActionResource,
            Assembly.GetExecutingAssembly().GetManifestResourceNames());
    }

    // Program.IsHelpRequest and the version check mirror three comparisons
    // from Action.detectSpecialCase. The name list above cannot see a
    // change to those, so pin their text directly.
    [Fact]
    public void SpecialCaseHandlingIsUnchanged()
    {
        var source = ReadActionSource();
        var start = source.IndexOf("pub fn detectSpecialCase", StringComparison.Ordinal);
        Assert.True(start >= 0, "detectSpecialCase is gone from src/cli/ghostty.zig.");
        var body = source[start..];

        foreach (var expected in new[]
                 {
                     @"std.mem.eql(u8, arg, ""-e"")",
                     @"std.mem.eql(u8, arg, ""--version"")",
                     @"std.mem.eql(u8, arg, ""--help"")",
                     @"std.mem.eql(u8, arg, ""-h"")",
                 })
        {
            Assert.True(
                body.Contains(expected, StringComparison.Ordinal),
                $"Upstream changed CLI special-case handling: {expected} is gone " +
                "from Action.detectSpecialCase. Re-check Program.IsHelpRequest and " +
                "the version interception in Program.MainImpl.");
        }
    }

    private static HashSet<string> ReadActionFields()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in EnumBlockLines())
        {
            var match = FieldPattern.Match(line);
            if (match.Success) names.Add(match.Groups["name"].Value);
        }
        return names;
    }

    private static IEnumerable<string> EnumBlockLines()
    {
        var source = ReadActionSource();

        var open = source.IndexOf(EnumOpen, StringComparison.Ordinal);
        Assert.True(open >= 0, $"'{EnumOpen}' not found in src/cli/ghostty.zig.");
        var bodyStart = open + EnumOpen.Length;

        var decl = DeclStart.Match(source, bodyStart);
        Assert.True(decl.Success, "No `pub fn` closes the Action enum field block.");

        return source[bodyStart..decl.Index]
            .Split('\n')
            .Select(l => l.TrimEnd('\r'));
    }

    private static string ReadActionSource()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ActionResource);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
