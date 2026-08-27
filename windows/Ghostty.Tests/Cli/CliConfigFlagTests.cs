using Ghostty.Core.Cli;
using Xunit;

namespace Ghostty.Tests.Cli;

// Behaviour of the config-flag rewrite. --no-config is translated into the
// libghostty key that performs the discard, rather than acted on here,
// because the discard is defined by a replay marker inside loadCliArgs that
// nothing outside libghostty can reproduce.
public class CliConfigFlagTests
{
    [Theory]
    [InlineData(@"wintty.exe --no-config",
                @"wintty.exe --config-default-files=false")]
    // Position is not special: the flag is found wherever it sits.
    [InlineData(@"wintty.exe --font-size=12 --no-config",
                @"wintty.exe --font-size=12 --config-default-files=false")]
    // Separator runs are preserved verbatim, so the surrounding line is
    // spliced rather than rebuilt.
    [InlineData("wintty.exe  --no-config\t--font-size=12",
                "wintty.exe  --config-default-files=false\t--font-size=12")]
    // argv[0] quoting: the space inside the path is not a separator, so the
    // scan does not start an argument early and splice at the wrong offset.
    [InlineData(@"""C:\Program Files\Wintty\wintty.exe"" --no-config",
                @"""C:\Program Files\Wintty\wintty.exe"" --config-default-files=false")]
    // Repeated is harmless, and every occurrence is translated: leaving one
    // behind would hand libghostty an unknown key.
    [InlineData(@"wintty.exe --no-config --no-config",
                @"wintty.exe --config-default-files=false --config-default-files=false")]
    public void RewritesNoConfigIntoTheLibghosttyKey(string commandLine, string expected)
    {
        var result = CliAliases.RewriteConfigFlags(commandLine);
        Assert.Equal(expected, result.CommandLine);
        Assert.True(result.NoConfig);
        Assert.True(result.Any);
    }

    [Theory]
    // No flag: the string is handed back as-is.
    [InlineData(@"wintty.exe")]
    [InlineData(@"wintty.exe --font-size=12")]
    // -e hands the rest of the line to the child command, so a flag after it
    // configures that command and not Wintty.
    [InlineData(@"wintty.exe -e mytool --no-config")]
    // A prefix of the flag is not the flag.
    [InlineData(@"wintty.exe --no-config-thing")]
    // Inside a quoted argument it is one value, not a flag.
    [InlineData(@"wintty.exe --title=""x --no-config y""")]
    public void LeavesTheCommandLineAloneWithoutTheFlag(string commandLine)
    {
        var result = CliAliases.RewriteConfigFlags(commandLine);
        Assert.Equal(commandLine, result.CommandLine);
        Assert.False(result.NoConfig);
    }

    [Theory]
    // The documented form.
    [InlineData(@"wintty.exe --config-file=C:\cfg\a.wintty")]
    // Backslashes and quotes in the value do not stop the flag name matching,
    // which is the whole reason only the name is compared.
    [InlineData(@"wintty.exe --config-file=""C:\Program Files\a.wintty""")]
    // Bare, with the value as a separate argument.
    [InlineData(@"wintty.exe --config-file a.wintty")]
    // Alongside the other flag.
    [InlineData(@"wintty.exe --no-config --config-file=a.wintty")]
    public void DetectsConfigFile(string commandLine)
    {
        var result = CliAliases.RewriteConfigFlags(commandLine);
        Assert.True(result.ConfigFile);
        Assert.True(result.Any);
    }

    [Theory]
    [InlineData(@"wintty.exe --config-files=a")]
    [InlineData(@"wintty.exe --config-file-thing=a")]
    [InlineData(@"wintty.exe -e mytool --config-file=a")]
    [InlineData(@"wintty.exe --font-size=12")]
    public void DoesNotSeeConfigFileWhereThereIsNone(string commandLine)
    {
        Assert.False(CliAliases.RewriteConfigFlags(commandLine).ConfigFile);
    }

    [Fact]
    public void AnIntactLaunchIsReportedAsWantingNothing()
    {
        var result = CliAliases.RewriteConfigFlags(@"wintty.exe");
        Assert.False(result.Any);
        Assert.False(result.NoConfig);
        Assert.False(result.ConfigFile);
    }

    // The two rewrites compose: the bare-subcommand splice runs first and
    // moves every later offset, so the config scan has to re-tokenize rather
    // than reuse anything from it.
    [Fact]
    public void ComposesWithTheBareSubcommandRewrite()
    {
        const string commandLine = @"wintty.exe show-config --no-config";
        Assert.True(CliAliases.TryRewrite(commandLine, out var rewritten, out _));

        var result = CliAliases.RewriteConfigFlags(rewritten);
        Assert.Equal(@"wintty.exe +show-config --config-default-files=false", result.CommandLine);
        Assert.True(result.NoConfig);
    }

    // The help text has to state the asymmetry, because a user who reads
    // "--config-file" as the mirror of "--no-config" will expect it to
    // supply the Windows-only keys, and it cannot.
    [Fact]
    public void HelpDocumentsBothFlagsAndTheirAsymmetry()
    {
        var help = CliAliases.RenderHelp("wintty");

        Assert.Contains("--no-config", help);
        Assert.Contains("--config-file=<path>", help);
        Assert.Contains("vertical-tabs", help);
        Assert.Contains("separate instance", help);
    }
}
