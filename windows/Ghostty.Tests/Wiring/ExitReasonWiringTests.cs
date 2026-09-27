using System.Linq;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That every exit names its reason (#967, tracker row D13).
///
/// The shell assembly cannot be loaded into a test host, so the exit
/// sites are only visible to source. The guards pair each clean
/// <c>Environment.Exit</c> with the <c>ExitReason.Log</c> written
/// immediately before it and pin the pair by position, so a site that
/// loses its line, loses its order or gains a bare sibling reds by name.
/// The three sites that already write their own reason on stderr
/// (UsageError, TestConfigRefused, InitFailed) are pinned as the exempt
/// set rather than silently left out.
/// </summary>
public class ExitReasonWiringTests
{
    private const string Wired = "ExitReason.Log";
    private const string Bare = "Environment.Exit";

    [Fact]
    public void Every_clean_exit_in_MainImpl_names_its_reason()
    {
        var main = ShellSource.Load("Program.cs").Method("MainImpl");
        var logs = main.Calls(Wired);
        var exits = main.Calls(Bare);

        // The census first: five wired exits plus the one that writes its
        // own reason. A new exit anywhere in MainImpl must carry its
        // reason line or join the named-reason set - and both counts
        // updated deliberately, never silently.
        Assert.True(exits.Count == 6,
            $"expected 6 '{Bare}' in MainImpl (5 wired + UsageError), found {exits.Count}; " +
            "a new exit site needs an ExitReason.Log beside it");
        Assert.True(logs.Count == 5,
            $"expected 5 '{Wired}' in MainImpl, found {logs.Count}; a wired exit lost its reason line");

        // The exempt exit is pinned by its own code spelling: UsageError,
        // whose reason is the WriteStderr the site writes before exiting.
        var exempt = exits.Where(e => e.Arg(0).Contains("ExitCode.")).ToList();
        Assert.True(exempt.Count == 1,
            $"expected exactly 1 named-reason exit in MainImpl, found {exempt.Count}");
        Assert.Equal("(int)ExitCode.UsageError", exempt[0].Arg(0));
        Assert.True(
            main.Calls("WriteStderr").Any(w => w.SpanStart < exempt[0].SpanStart),
            "the UsageError exit must have its own reason written before it");

        // Pairing by position: in source order, each clean exit's reason
        // line sits after the previous exit site and before the exit it
        // names. Reordering a pair (line after exit) or dropping one
        // (an exit whose would-be line is the next site's) reds here.
        var pairs = logs.OrderBy(l => l.SpanStart)
            .Zip(exits.Where(e => !e.Arg(0).Contains("ExitCode.")).OrderBy(e => e.SpanStart),
                (log, exit) => (Log: log, Exit: exit))
            .ToList();
        Assert.True(pairs.Count == 5, "expected 5 wired exit pairs in MainImpl");
        var previousExitStart = 0;
        foreach (var (log, exit) in pairs)
        {
            Assert.True(log.SpanStart > previousExitStart,
                "a reason line drifted before the previous exit site; it no longer pairs with the exit it names");
            Assert.True(log.SpanStart < exit.SpanStart,
                "the reason line must be written before the exit it names");
            previousExitStart = exit.SpanStart;
        }

        // The pairs are pinned by site, code variable and a fragment of
        // the reason - a swapped `why` between two sites is a real
        // misattribution and should not walk through.
        var (version, crash, help, themes, action) = (pairs[0], pairs[1], pairs[2], pairs[3], pairs[4]);
        Assert.Equal("versionExit", version.Log.Arg(0));
        Assert.Contains("version", version.Log.Arg(1));
        Assert.Equal("versionExit", version.Exit.Arg(0));
        Assert.Equal("crashExit", crash.Log.Arg(0));
        Assert.Contains("crash trigger", crash.Log.Arg(1));
        Assert.Equal("crashExit", crash.Exit.Arg(0));
        Assert.Equal("0", help.Log.Arg(0));
        Assert.Contains("help", help.Log.Arg(1));
        Assert.Equal("0", help.Exit.Arg(0));
        Assert.Equal("0", themes.Log.Arg(0));
        Assert.Contains("forwarded", themes.Log.Arg(1));
        Assert.Equal("0", themes.Exit.Arg(0));
        Assert.Equal("exitCode", action.Log.Arg(0));
        Assert.Contains("cli action", action.Log.Arg(1));
        Assert.Equal("exitCode", action.Exit.Arg(0));
    }

    [Fact]
    public void The_named_reason_exits_outside_MainImpl_write_their_own_reasons()
    {
        // TestConfigRefused and InitFailed already report on stderr before
        // exiting; they are the reason the census below is 8 and not 10.
        // Pinning them keeps the exempt set closed: a new exit that lands
        // in one of these methods still has to report first.
        var program = ShellSource.Load("Program.cs");
        foreach (var name in new[] { "RefuseTestConfigStart", "InitGhostty" })
        {
            var method = program.Method(name);
            var exit = Assert.Single(method.Calls(Bare));
            Assert.True(exit.Arg(0).Contains("ExitCode."),
                $"expected {name}'s exit to be a named-reason site");
            Assert.True(
                method.Calls("WriteStderr").Concat(method.Calls("WriteStartupDiagnostic"))
                    .Any(w => w.SpanStart < exit.SpanStart),
                $"{name} must write its reason before exiting");
        }
    }

    [Fact]
    public void The_forwarded_launch_exit_names_its_reason()
    {
        // The one GUI exit: a second launch forwarded to the running
        // instance used to vanish with an exit 0 and nothing in any log.
        var forward = ShellSource.Load("App.xaml.cs").Method("ForwardLaunchToPrimary");
        var exit = Assert.Single(forward.Calls(Bare));
        var log = Assert.Single(forward.Calls(Wired));
        Assert.True(log.SpanStart < exit.SpanStart,
            "the reason line must be written before the forwarded launch exits");
        Assert.Equal("0", log.Arg(0));
        Assert.Contains("forwarded", log.Arg(1));
    }

    [Fact]
    public void Every_Program_exit_site_is_accounted_for()
    {
        var program = ShellSource.Load("Program.cs").Root;
        var exits = program.Calls(Bare);
        var wired = program.Calls(Wired);

        Assert.True(exits.Count == 8,
            $"expected 8 '{Bare}' in Program.cs (6 MainImpl, 1 RefuseTestConfigStart, " +
            $"1 InitGhostty), found {exits.Count}. A new exit site needs an " +
            "ExitReason.Log beside it - or, if it writes its own reason, a guard " +
            "in this class - and this census updated deliberately.");
        Assert.True(wired.Count == 5,
            $"expected 5 '{Wired}' in Program.cs, found {wired.Count}");
    }
}
