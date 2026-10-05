using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ghostty.Tests.Wiring;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// That <c>windows-settings-ui</c> ships on.
///
/// The graphical settings are the product default (a founder decision),
/// so the Zig schema says so, the app's own reader of the key agrees, and
/// the one launch that must still not open a settings window says so for a
/// reason of its own.
///
/// Three places carry the default, and a flip in only the first is the
/// failure this file exists to catch:
///
///   - <c>src/config/Config.zig</c>, the schema every parser and every CLI
///     flag resolves against. Read here through the embedded copy, the same
///     tripwire the curated-default keybind pins use, so a dotnet-only run
///     can see a Zig-side default move.
///   - <c>ConfigService.SettingsUiEnabled</c>, which reads the key off the
///     raw config file with a fallback string of its own. libghostty parses
///     this key, so nothing syncs the two, and the flip in the schema alone
///     leaves the app answering "off" to a config that never said so.
///   - The <c>--no-config</c> rule. With the key on by default, the empty
///     file cache under that flag would read as "on", and a settings window
///     whose toggles write to a config file this launch is ignoring is the
///     confusing outcome the old default was free to avoid.
///
/// The WinUI assembly cannot be loaded into a test host, so the C# half of
/// this is read off the source like every other wiring guard.
/// </summary>
public class SettingsUiDefaultTests
{
    /// <summary>The Zig schema, embedded by Ghostty.Tests.csproj.</summary>
    private const string ConfigResource = "Ghostty.Tests.Config.Defaults.Config.zig";

    /// <summary>The key whose default this batch flips.</summary>
    private const string Key = "windows-settings-ui";

    /// <summary>Where the app reads the key back out of the config file.</summary>
    private const string ServiceFile = "Services.ConfigService.cs";

    [Fact]
    public void TheZigSchemaDefaultsTheSettingsUiOn()
    {
        var config = ReadResource(ConfigResource);

        Assert.True(
            config.Contains(
                $@"@""{Key}"": bool = true,", StringComparison.Ordinal),
            $@"src/config/Config.zig must ship `@""{Key}"": bool = true`. The "
            + "graphical settings are the product default; every parser, CLI "
            + "flag and doc page resolves against this line and nowhere else.");
    }

    [Fact]
    public void TheSchemaDocCommentSaysItIsOnByDefault()
    {
        var doc = SchemaDocComment();

        // The doc comment is the only place a user reads what the default is,
        // and the generated docs and the man page are built from it.
        Assert.True(
            doc.Contains("default", StringComparison.OrdinalIgnoreCase),
            $"the {Key} doc comment must state the default it ships with; found:\n{doc}");
        Assert.True(
            !doc.Contains("disabled by default", StringComparison.OrdinalIgnoreCase),
            $"the {Key} doc comment still says the UI is off by default, which is "
            + "the decision this batch reverses");
    }

    [Fact]
    public void TheAppReadsTheSameDefaultTheSchemaShips()
    {
        var read = SettingsUiRead();

        Assert.True(
            read.FileValueFallback == "true",
            $"ConfigService must read {Key} with a \"true\" fallback to match the "
            + "Zig schema; found \"" + read.FileValueFallback + "\". libghostty parses "
            + "this key, so no unknown-field path reconciles the two: a schema that "
            + "says true and a reader that says false means the app still opens the "
            + "config file in notepad for every user who never set the key.");
    }

    [Fact]
    public void NoConfigStillMeansNoSettingsWindow()
    {
        var read = SettingsUiRead();

        Assert.True(
            read.GuardedByNoConfig,
            $"the {Key} read must be guarded by _noConfig. With the key on by "
            + "default an empty file cache under --no-config reads as on, and a "
            + "settings window whose toggles write to a file this launch is "
            + "ignoring is worse than the external editor it replaced.");
    }

    [Fact]
    public void TheGuardIsTheNoConfigFlagAndNothingElse()
    {
        // A second condition on this read is a second policy about when the
        // product default does not apply, and nobody would know which won.
        var read = SettingsUiRead();

        Assert.False(
            read.GuardedByConfigPath,
            "the " + Key + " read must not branch on the config file existing. "
            + "A user whose config file has not been written yet gets the "
            + "product default and a seed write, not the old text-editor "
            + "behaviour.");
    }

    [Fact]
    public void TheKeyIsStillAZigFieldAndNotAWindowsOnlyKey()
    {
        // libghostty parses the key, so it must stay out of the Windows-only
        // registry: registering it would swallow the genuine "value required"
        // / "invalid value" diagnostics a bad line would otherwise produce.
        var keys = ShellSource.Load("Core.Config.WindowsOnlyKeys.cs");

        Assert.True(
            keys.Root.DescendantNodes()
                .OfType<LiteralExpressionSyntax>()
                .Select(l => l.Token.ValueText)
                .All(text => text != Key),
            $"{Key} moved into WindowsOnlyKeys. It is a real Zig field, so "
            + "registering it hides the parse errors a bad value produces.");
    }

    /// <summary>
    /// The two facts about the app-side read, pulled off the assignment.
    /// </summary>
    private static (string FileValueFallback, bool GuardedByNoConfig, bool GuardedByConfigPath)
        SettingsUiRead()
    {
        var source = ShellSource.Load(ServiceFile);
        var assignments = source.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "SettingsUiEnabled")
            .ToList();
        var assignment = Assert.Single(assignments);

        // The fallback is the SECOND argument of the GetFileValue call. The
        // first is the key, and a filter on the key text alone would read the
        // key back as the default and pass on a reader that never flipped.
        var read = Assert.Single(
            assignment.Right.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Where(i => i.CalleeText() == "GetFileValue"));
        // Arg() is source text, so the quotes are still on it.
        Assert.Equal($"\"{Key}\"", read.Arg(0));
        var fileValueFallback = Assert.IsType<LiteralExpressionSyntax>(
            read.ArgExpression(1)).Token.Value as string;

        // The guard is on the RIGHT of the assignment, not an ancestor of it:
        // `SettingsUiEnabled = !_noConfig && ...` puts the && where an
        // ancestor walk from the assignment never looks.
        var guard = assignment.Right.DescendantNodesAndSelf()
            .OfType<BinaryExpressionSyntax>()
            .FirstOrDefault();
        var guardedByNoConfig =
            guard is not null
            && guard.IsKind(SyntaxKind.LogicalAndExpression)
            && guard.ToString().Contains("_noConfig", StringComparison.Ordinal);
        var guardedByConfigPath = guard is not null
            && guard.ToString().Contains("ConfigFilePath", StringComparison.Ordinal);

        return (fileValueFallback, guardedByNoConfig, guardedByConfigPath);
    }

    /// <summary>
    /// The whole contiguous doc comment above the key's declaration.
    /// </summary>
    private static string SchemaDocComment()
    {
        var config = ReadResource(ConfigResource);
        var lines = config.Split('\n');
        var at = Array.FindIndex(lines, l => l.Contains($"@\"{Key}\"", StringComparison.Ordinal));
        Assert.True(at >= 0, $"Config.zig does not declare {Key}");

        var first = at;
        while (first > 0 && lines[first - 1].TrimStart().StartsWith("///", StringComparison.Ordinal))
        {
            first--;
        }

        return string.Join('\n', lines[first..at]);
    }

    private static string ReadResource(string resource)
    {
        using var stream = typeof(SettingsUiDefaultTests).Assembly
            .GetManifestResourceStream(resource);
        Assert.True(stream is not null, $"{resource} is not embedded; see Ghostty.Tests.csproj");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
