using System.Linq;
using Ghostty.Core.Cli;
using Xunit;

namespace Ghostty.Tests.Cli;

// Behaviour of the bare-subcommand rewrite. The tokenizer under test
// mirrors std.process.Args.Iterator.Windows, so the cases that matter are
// the ones where a naive "split on whitespace" would disagree with what
// libghostty actually parses out of the same string.
public class CliAliasTests
{
    [Theory]
    // The point of the feature.
    [InlineData(@"wintty.exe list-themes", @"wintty.exe +list-themes")]
    [InlineData(@"wintty.exe ssh user@host", @"wintty.exe +ssh user@host")]
    // argv[0] quoting: the space inside the path is not a separator.
    [InlineData(@"""C:\Program Files\Wintty\wintty.exe"" list-themes",
                @"""C:\Program Files\Wintty\wintty.exe"" +list-themes")]
    // Tab is a separator; the run of spaces is preserved verbatim.
    [InlineData("wintty.exe\tlist-themes", "wintty.exe\t+list-themes")]
    [InlineData(@"wintty.exe   list-themes", @"wintty.exe   +list-themes")]
    // Everything past the first argument is untouched.
    [InlineData(@"wintty.exe show-config --font-size=12 ""a b""",
                @"wintty.exe +show-config --font-size=12 ""a b""")]
    public void RewritesBareSubcommand(string commandLine, string expected)
    {
        Assert.True(CliAliases.TryRewrite(commandLine, out var rewritten, out var action));
        Assert.Equal(expected, rewritten);
        Assert.NotNull(action);
    }

    [Theory]
    // Already in +action form.
    [InlineData(@"wintty.exe +list-themes")]
    // -e hands the line to a child command.
    [InlineData(@"wintty.exe -e ssh myhost")]
    [InlineData(@"wintty.exe notacommand")]
    // Quote or backslash in the token: declined rather than guessed at.
    [InlineData(@"wintty.exe ""list-themes""")]
    [InlineData(@"wintty.exe \""list-themes\""")]
    // No unquoted separator at all, so this is a single argv[0].
    [InlineData(@"""wintty.exe""list-themes")]
    // No arguments.
    [InlineData(@"wintty.exe")]
    [InlineData(@"wintty.exe ")]
    [InlineData("")]
    [InlineData("   ")]
    // Only space and tab separate arguments on Windows. VT, LF and CR are
    // ordinary characters, so these are all one argv[0].
    [InlineData("wintty.exe\vlist-themes")]
    [InlineData("wintty.exe\nlist-themes")]
    [InlineData("wintty.exe\rlist-themes")]
    // NUL terminates the command line before any argument.
    [InlineData("wintty.exe\0list-themes")]
    // A leading separator makes argv[0] empty, so the exe path becomes the
    // first argument and is not an action.
    [InlineData(@" wintty.exe list-themes")]
    public void LeavesEverythingElseAlone(string commandLine)
    {
        Assert.False(CliAliases.TryRewrite(commandLine, out var rewritten, out var action));
        Assert.Equal(commandLine, rewritten);
        Assert.Null(action);
    }

    [Fact]
    public void PreservesUnpairedSurrogatesInLaterArguments()
    {
        // A lone high surrogate is representable in a Windows command line
        // and in a .NET string, and libghostty round-trips it through
        // WTF-8. Splicing must not disturb it.
        var commandLine = "wintty.exe show-config --config-file=\ud800tail";

        Assert.True(CliAliases.TryRewrite(commandLine, out var rewritten, out _));
        Assert.Equal("wintty.exe +show-config --config-file=\ud800tail", rewritten);
    }

    // The invariant the dispatch gate in Program.MainImpl depends on: if
    // TryRewrite reports an alias, libghostty is guaranteed to receive the
    // +action form. A future action name the tokenizer declines to splice
    // would fail here rather than gating a command open that does nothing.
    [Fact]
    public void EveryActionRewritesFromABareFirstArgument()
    {
        foreach (var name in CliAliases.Actions)
        {
            Assert.True(
                CliAliases.TryRewrite($"wintty.exe {name}", out var rewritten, out var action),
                $"'{name}' is in Actions but does not rewrite from a bare first argument.");
            Assert.Equal($"wintty.exe +{name}", rewritten);
            Assert.Equal(name, action);
        }
    }

    [Theory]
    [InlineData("list-themes", true)]
    [InlineData("boo", true)]
    [InlineData("notacommand", true)]
    [InlineData("list-theme", true)]
    // Flags, paths and anything capitalised are not mistyped verbs.
    [InlineData("--font-size=12", false)]
    [InlineData("-e", false)]
    [InlineData("/?", false)]
    [InlineData(@"C:\tmp\a.txt", false)]
    [InlineData("List-Themes", false)]
    [InlineData("list_themes", false)]
    [InlineData("2fast", false)]
    [InlineData("", false)]
    public void LooksLikeCommandMatchesBareVerbsOnly(string arg, bool expected)
        => Assert.Equal(expected, CliAliases.LooksLikeCommand(arg));

    [Theory]
    // Bare and + spellings of the help command itself.
    [InlineData(true, "help")]
    [InlineData(true, "+help")]
    // Fallback flags, with no action present.
    [InlineData(true, "--help")]
    [InlineData(true, "-h")]
    [InlineData(true, "/?")]
    [InlineData(true, "--font-size=12", "--help")]
    // An action owns --help: only libghostty can render per-action help.
    [InlineData(false, "+list-themes", "--help")]
    [InlineData(false, "list-themes", "--help")]
    // -e hands the line to a child command, so the flag is the child's.
    // The second case is the ordering that matters: upstream's
    // abort_if_no_action fires on -e even though --help came first.
    [InlineData(false, "-e", "pwsh", "--help")]
    [InlineData(false, "--help", "-e", "pwsh")]
    [InlineData(false, "-e", "pwsh")]
    [InlineData(false, "notacommand")]
    public void IsHelpRequestMatchesUpstreamFallbackRules(bool expected, params string[] args)
    {
        // Mirrors what Program.MainImpl passes: the alias flag comes from
        // TryRewrite over the same invocation.
        var isAlias = args.Length > 0 && CliAliases.Actions.Contains(args[0]);
        Assert.Equal(expected, CliAliases.IsHelpRequest(args, isAlias));
    }

    [Fact]
    public void IsHelpRequestIgnoresEmptyArgs()
        => Assert.False(CliAliases.IsHelpRequest([], false));

    [Fact]
    public void HelpListsEveryActionAndBothSpellings()
    {
        var help = CliAliases.RenderHelp("wintty");

        foreach (var name in CliAliases.Actions)
            Assert.Contains($"  {name}\n", help);

        // The whole point is telling the reader both forms exist.
        Assert.Contains("+command", help);
        Assert.Contains("wintty +list-themes", help);

        // Upstream's help text is wrong on Windows in these three specific
        // ways; regressing to it would be silent otherwise.
        Assert.DoesNotContain("ghostty [", help);
        Assert.DoesNotContain("open -na", help);
        Assert.DoesNotContain("src/config/Config.zig", help);
    }

    [Fact]
    public void HelpUsesTheProgramNameItIsGiven()
        => Assert.Contains("Usage: wt [command]", CliAliases.RenderHelp("wt"));

    [Fact]
    public void ActionsAreSortedInHelp()
    {
        var help = CliAliases.RenderHelp("wintty");
        var offsets = CliAliases.Actions
            .OrderBy(static n => n, System.StringComparer.Ordinal)
            .Select(n => help.IndexOf($"  {n}\n", System.StringComparison.Ordinal))
            .ToList();

        Assert.DoesNotContain(-1, offsets);
        Assert.Equal(offsets.OrderBy(static o => o), offsets);
    }
}
