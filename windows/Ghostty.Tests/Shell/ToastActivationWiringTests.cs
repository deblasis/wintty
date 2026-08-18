using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Guards the shell-side wiring of toast activation by reading the source as
/// text, the way <c>TrayIconWiringTests</c> does. This project deliberately
/// does not reference Ghostty.csproj (it would drag the WinAppSDK MRT/PRI
/// targets into a plain net10.0 assembly), so the decision logic lives in
/// Ghostty.Core and is unit-tested there -- but the wiring that connects it to
/// WinRT cannot be, and unguarded wiring is how the whole feature silently
/// stops working while every unit test stays green.
///
/// These assertions are deliberately few and are about ORDER and PRESENCE of
/// facts that are load-bearing and non-obvious. They are not a substitute for
/// exercising a real toast click on a packaged build.
/// </summary>
public class ToastActivationWiringTests
{
    // The WindowsAppSDK contract: a handler attached after Register() throws
    // ERROR_NOT_FOUND, and Register() picks its COM class-registration flag
    // from whether a handler exists at that instant -- registering single-use
    // when none does, which makes a click spawn a second process instead of
    // reaching the running one. Neither failure is visible at compile time and
    // neither reproduces without a packaged install, so the order is pinned
    // here.
    [Fact]
    public void NotificationInvoked_IsSubscribedBeforeRegister()
    {
        var app = ReadEmbedded("App.xaml.cs");

        var subscribe = app.IndexOf("NotificationInvoked +=", StringComparison.Ordinal);
        var register = app.IndexOf(
            "AppNotificationManager.Default.Register()", StringComparison.Ordinal);

        Assert.True(subscribe >= 0, "no NotificationInvoked subscription in App.xaml.cs");
        Assert.True(register >= 0, "no AppNotificationManager.Default.Register() in App.xaml.cs");
        Assert.True(
            subscribe < register,
            "NotificationInvoked must be subscribed before Register(): a late subscribe "
            + "throws ERROR_NOT_FOUND, and Register() with no handler attached registers "
            + "the COM activator single-use.");
    }

    // Register() is a process-wide registration; a second call is not additive.
    [Fact]
    public void Register_IsCalledExactlyOnce()
    {
        var app = ReadEmbedded("App.xaml.cs");

        var occurrences = CountOccurrences(app, "AppNotificationManager.Default.Register()");

        Assert.Equal(1, occurrences);
    }

    // The activation probe has to run before the single-instance gate, because
    // a secondary forwards its launch and exits there: anything read after it
    // never reaches a secondary at all, and the click degrades to a bare
    // window.
    [Fact]
    public void ActivationIsProbedBeforeTheSingleInstanceGate()
    {
        var app = ReadEmbedded("App.xaml.cs");

        var probe = app.IndexOf("ProbeActivation()", StringComparison.Ordinal);
        var gate = app.IndexOf("HandleSingleInstanceGate(", StringComparison.Ordinal);

        Assert.True(probe >= 0, "OnLaunched no longer probes the activation");
        Assert.True(gate >= 0, "OnLaunched no longer runs the single-instance gate");
        Assert.True(
            probe < gate,
            "the activation probe must run before the single-instance gate, or a "
            + "secondary exits at the gate without ever reading its own activation.");
    }

    // Reading the AppNotification kind off GetActivatedEventArgs is what makes
    // the forward independent of when WinAppSDK chooses to dispatch
    // NotificationInvoked. Losing it reintroduces a timing assumption that
    // cannot be verified without a packaged build.
    [Fact]
    public void ProbeReadsBothProtocolAndAppNotificationKinds()
    {
        var app = ReadEmbedded("App.xaml.cs");
        var probe = Section(app, "private static Uri? ProbeActivation()");

        Assert.Contains("ExtendedActivationKind.Protocol", probe);
        Assert.Contains("ExtendedActivationKind.AppNotification", probe);
        Assert.Contains("ToastActivations.Note(", probe);
    }

    // The argv scan is the fallback for a probe that THREW, so it cannot live
    // inside the probe's try block. Pinned by requiring the resolve to come
    // after the catch.
    [Fact]
    public void UriFallbackRunsAfterTheProbeCatch()
    {
        var app = ReadEmbedded("App.xaml.cs");
        var probe = Section(app, "private static Uri? ProbeActivation()");

        var catchBlock = probe.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var resolve = probe.IndexOf("ProtocolLaunch.Resolve(", StringComparison.Ordinal);

        Assert.True(catchBlock >= 0, "the probe no longer guards GetActivatedEventArgs");
        Assert.True(resolve >= 0, "the probe no longer resolves the --uri fallback");
        Assert.True(
            resolve > catchBlock,
            "the --uri fallback must run after the probe's catch: nested inside the try "
            + "it is unreachable exactly when GetActivatedEventArgs throws, which is the "
            + "case it exists to cover.");
    }

    // The forward is the only record a secondary leaves before exiting, and
    // ForwardedArgv is what strips a marker a user typed so a command line
    // cannot fabricate a click.
    [Fact]
    public void ForwardCarriesTheLatchedActivationThroughForwardedArgv()
    {
        var app = ReadEmbedded("App.xaml.cs");
        var forward = Section(app, "private void ForwardLaunchToPrimary(string pipeName)");

        Assert.Contains("ToastActivation.ForwardedArgv(", forward);
        Assert.Contains("ToastActivations.Pending", forward);
    }

    // A forwarded marker naming a surface that is not here must fall through to
    // an ordinary launch, or the user loses the window (and any jump-list
    // action) they actually asked for.
    [Fact]
    public void ForwardedLaunchChecksLivenessAndFallsThrough()
    {
        var app = ReadEmbedded("App.xaml.cs");
        var open = Section(app, "internal void OpenWindowFromLaunch(");

        var liveness = open.IndexOf("AnyWindowHasToastSurface(", StringComparison.Ordinal);
        var fallthrough = open.IndexOf("HandleJumpListLaunch(", StringComparison.Ordinal);

        Assert.True(liveness >= 0, "a forwarded activation is honoured without checking it can be");
        Assert.True(fallthrough >= 0, "a forwarded launch no longer falls through to a normal launch");
        Assert.True(liveness < fallthrough);
    }

    // The promise a notification click makes is that the app comes forward.
    // The fallback therefore has to sit outside the try that wraps the scan,
    // so a throwing scan does not swallow it too.
    [Fact]
    public void ToastConsumerFallsBackToPlainActivationOutsideTheScanTry()
    {
        var app = ReadEmbedded("App.xaml.cs");
        var consumer = Section(app, "private void OnToastActivated(");

        var scan = consumer.IndexOf("TryFocusToastSurface(", StringComparison.Ordinal);
        var guard = consumer.IndexOf("if (focused) return;", StringComparison.Ordinal);
        var fallback = consumer.IndexOf("ShowOrFocusWindowsFromTray()", StringComparison.Ordinal);

        Assert.True(scan >= 0, "the toast consumer no longer looks for the surface");
        Assert.True(guard >= 0, "the toast consumer no longer separates the scan from its fallback");
        Assert.True(fallback > guard, "the fallback must run after, and outside, the scan's try");
    }

    // Both handles are process-lifetime singletons, so a subscription left
    // attached roots App for the life of the process.
    [Fact]
    public void TeardownDetachesBothToastSubscriptions()
    {
        var app = ReadEmbedded("App.xaml.cs");

        Assert.Contains("NotificationInvoked -= OnToastNotificationInvoked", app);
        Assert.Contains("ToastActivations.Reset()", app);
    }

    // Without an argument the activation callback sees only "the app was
    // clicked", never which pane asked for attention.
    [Fact]
    public void ToastCarriesTheSurfaceAsALaunchArgument()
    {
        var notifier = ReadEmbedded("AppNotificationToastNotifier.cs");

        Assert.Contains("AddArgument(", notifier);
        Assert.Contains("ToastActivation.SurfaceArgumentKey", notifier);
    }

    // The quick terminal's only legal reveal is its own Show(), which
    // re-positions, runs the clip/slide reveal and arms autohide. A bare
    // AppWindow.Show() leaves the last animation frame applied, so the window
    // takes keyboard focus while the user sees nothing.
    [Fact]
    public void QuickTerminalIsRevealedThroughItsOwnShowPath()
    {
        var window = ReadEmbedded("MainWindow.xaml.cs");
        var reveal = Section(window, "private void RevealForActivation()");

        var quakeBranch = reveal.IndexOf("IsQuickTerminal", StringComparison.Ordinal);
        var quakeShow = reveal.IndexOf("Show();", StringComparison.Ordinal);
        var plainShow = reveal.IndexOf("AppWindow.Show()", StringComparison.Ordinal);

        Assert.True(quakeBranch >= 0, "the reveal no longer distinguishes the quick terminal");
        Assert.True(quakeShow >= 0, "the quick terminal is no longer revealed through Show()");
        Assert.True(
            quakeShow < plainShow,
            "the quick terminal branch must be taken before the bare AppWindow.Show().");
    }

    // Focus has to wait a tick: the tab swap and the activation both rebuild
    // the visual tree, and Focus on an unrealized element silently does
    // nothing.
    [Fact]
    public void ToastFocusIsDeferredAfterTheTabSwap()
    {
        var window = ReadEmbedded("MainWindow.xaml.cs");
        var focus = Section(window, "internal bool TryFocusToastSurface(string surfaceKey)");

        Assert.Contains("_tabManager.Activate(", focus);
        Assert.Contains("RevealForActivation()", focus);
        Assert.Contains("DispatcherQueue.TryEnqueue(", focus);
    }

    // Something other than the toggle put a window back on screen, so the
    // toggle's bookkeeping has to learn about it or the next press restores
    // instead of hiding.
    [Fact]
    public void RevealingAWindowForAToastUpdatesTheVisibilityToggleBookkeeping()
    {
        var app = ReadEmbedded("App.xaml.cs");
        var scan = Section(app, "private bool TryFocusToastSurface(string surfaceKey)");

        Assert.Contains("NoteWindowRevealed(", scan);

        var note = Section(app, "private void NoteWindowRevealed(MainWindow window)");
        Assert.Contains("_hiddenByVisibilityToggle.Remove(", note);
        Assert.Contains("_windowsHiddenByVisibilityToggle = false", note);
    }

    // One window mid-teardown throws RO_E_CLOSED out of AppWindow. Unguarded,
    // it ends the scan before a live window behind it is reached.
    [Fact]
    public void WindowScanIsGuardedPerWindow()
    {
        var app = ReadEmbedded("App.xaml.cs");
        var scan = Section(app, "private bool TryFocusToastSurface(string surfaceKey)");

        var loop = scan.IndexOf("foreach (var window", StringComparison.Ordinal);
        var guard = scan.IndexOf("catch (System.Exception", StringComparison.Ordinal);
        var carryOn = scan.IndexOf("continue;", guard < 0 ? 0 : guard, StringComparison.Ordinal);

        Assert.True(loop >= 0, "the scan no longer walks the windows");
        Assert.True(guard > loop, "the guard must be inside the loop, not around it");
        Assert.True(carryOn > guard, "a dead window must be skipped, not end the scan");
    }

    // Reads a member's body by brace matching, with comments stripped. Scoping
    // keeps each assertion about the method it names rather than passing on a
    // match somewhere else in a 1700-line file; stripping keeps prose from
    // satisfying an assertion about code, which matters here because these
    // methods are commented heavily enough to quote the very calls under test.
    private static string Section(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' is gone from the source");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no body found for '{declaration}'");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return StripComments(source[start..(i + 1)]);
            }
        }

        Assert.Fail($"unbalanced braces reading the body of '{declaration}'");
        return string.Empty;
    }

    // Drops whole comment lines, and a trailing comment only on lines with no
    // string literal -- a "//" inside a literal (a URL, say) is code, and
    // telling the two apart properly would need a lexer this does not warrant.
    private static string StripComments(string body)
    {
        var kept = body.Split('\n').Select(line =>
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) return string.Empty;
            if (line.Contains('"')) return line;
            var at = line.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? line : line[..at];
        });

        return string.Join('\n', kept);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }

    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
