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
/// Ghostty.Core and is unit-tested there -- while the wiring that connects it
/// to WinRT cannot be, and unguarded wiring is how a feature silently stops
/// working with every unit test still green.
///
/// What these can catch: a call deleted, or two calls whose ORDER is
/// load-bearing swapped. That is the whole of it.
///
/// What they cannot catch: whether any of it works. They do not run WinRT, do
/// not construct a window, and do not observe a click. A change that keeps the
/// shape and breaks the behaviour passes every one of them. Only a toast click
/// on a packaged, installed build tests that.
///
/// Every assertion reads source that has been through
/// <see cref="CSharpSourceText.Strip"/>. Assertions against raw file text are
/// defeatable by a comment or a diagnostic string that quotes the very
/// statement under test, and the comments in these methods do quote them.
/// </summary>
public class ToastActivationWiringTests
{
    // The WindowsAppSDK contract: a handler attached after Register() throws
    // ERROR_NOT_FOUND, and Register() picks its COM class-registration flag
    // from whether a handler exists at that instant -- registering single-use
    // when none does, which makes a click spawn a second process instead of
    // reaching the running one. Neither failure is visible at compile time and
    // neither reproduces without a packaged install, so the order is pinned.
    [Fact]
    public void NotificationInvoked_IsSubscribedBeforeRegister()
    {
        var launched = OnLaunched();

        var subscribe = CSharpSourceText.RequireIndex(
            launched, "NotificationInvoked +=",
            "OnLaunched no longer subscribes to NotificationInvoked");
        var register = CSharpSourceText.RequireIndex(
            launched, "AppNotificationManager.Default.Register()",
            "OnLaunched no longer registers for toast notifications");

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
        var app = Stripped("App.xaml.cs");

        Assert.Equal(1, CSharpSourceText.Count(app, "AppNotificationManager.Default.Register()"));
    }

    // The probe has to run before the single-instance gate, because a
    // secondary forwards its launch and exits there: anything read after the
    // gate never reaches a secondary at all, and its click degrades to a bare
    // window. Scoped to OnLaunched so these are the two CALL sites -- an
    // unscoped search finds the ProbeActivation declaration too, and would go
    // on passing after a reorder that broke the real thing.
    [Fact]
    public void ActivationIsProbedBeforeTheSingleInstanceGate()
    {
        var launched = OnLaunched();

        var probe = CSharpSourceText.RequireIndex(
            launched, "ProbeActivation()", "OnLaunched no longer probes the activation");
        var gate = CSharpSourceText.RequireIndex(
            launched, "HandleSingleInstanceGate(", "OnLaunched no longer runs the single-instance gate");

        Assert.True(
            probe < gate,
            "the activation probe must run before the single-instance gate, or a "
            + "secondary exits at the gate without ever reading its own activation.");
    }

    // Reading the AppNotification kind off GetActivatedEventArgs is what makes
    // the forward independent of when WinAppSDK dispatches NotificationInvoked.
    // Losing it reintroduces a timing assumption that cannot be verified
    // without a packaged build.
    [Fact]
    public void ProbeReadsBothProtocolAndAppNotificationKinds()
    {
        var probe = Member("App.xaml.cs", "private static Uri? ProbeActivation()");

        var protocol = CSharpSourceText.RequireIndex(
            probe, "ExtendedActivationKind.Protocol", "the probe no longer reads protocol activation");
        var appNotification = CSharpSourceText.RequireIndex(
            probe, "ExtendedActivationKind.AppNotification",
            "the probe no longer reads the toast activation kind, which is what makes the "
            + "forward independent of WinAppSDK dispatch timing");
        var note = CSharpSourceText.RequireIndex(
            probe, "TryNoteLaunchActivation(", "the probe reads the toast kind but records nothing");

        Assert.True(protocol < appNotification, "the protocol branch is expected first");
        Assert.True(note > appNotification, "the record must belong to the toast branch");
    }

    // The argv scan is the fallback for a probe that THREW, so it cannot live
    // inside the probe's try block.
    [Fact]
    public void UriFallbackRunsAfterTheProbeCatch()
    {
        var probe = Member("App.xaml.cs", "private static Uri? ProbeActivation()");

        var catchBlock = CSharpSourceText.RequireIndex(
            probe, "catch (Exception ex)", "the probe no longer guards GetActivatedEventArgs");
        var resolve = CSharpSourceText.RequireIndex(
            probe, "ProtocolLaunch.Resolve(", "the probe no longer resolves the --uri fallback");

        Assert.True(
            resolve > catchBlock,
            "the --uri fallback must run after the probe's catch: nested inside the try "
            + "it is unreachable exactly when GetActivatedEventArgs throws, which is the "
            + "case it exists to cover.");
    }

    // Both the probe and the notification callback can describe the same
    // launch click. Both go through TryNoteLaunchActivation so the relay can
    // dedupe on what the click IS -- the callback must not keep its own idea
    // of which one is the launch, because ordinal position is not identity and
    // guessing it swallowed real clicks.
    [Fact]
    public void LaunchActivationIsRecordedThroughTheDedupe()
    {
        var callback = Member("App.xaml.cs", "private void OnToastNotificationInvoked(");

        CSharpSourceText.RequireIndex(
            callback, "ToastActivations.TryNoteLaunchActivation(",
            "the callback no longer routes through the dedupe and a click can be acted on twice");
        Assert.Equal(0, CSharpSourceText.Count(callback, "ToastActivations.Note("));
    }

    // The launch window has to be closed once startup is over, or a real click
    // on the same surface the launch click named is dropped as a duplicate.
    // After the first subscriber, because that is when the launch click has
    // been acted on.
    [Fact]
    public void LaunchWindowIsClosedAfterTheFirstSubscriber()
    {
        var launched = OnLaunched();

        var subscribe = CSharpSourceText.RequireIndex(
            launched, "ToastActivations.Subscribe(", "OnLaunched no longer subscribes to toast clicks");
        var close = CSharpSourceText.RequireIndex(
            launched, "ToastActivations.CloseLaunchWindow()",
            "startup never declares the launch window over, so a click on the launch surface "
            + "is dropped as a duplicate");

        Assert.True(close > subscribe, "the window closes only once the launch click can be acted on");
    }

    // The forward is the only record a secondary leaves before exiting, and
    // ForwardedArgv is what keeps the reserved trailing slot honest.
    [Fact]
    public void ForwardCarriesTheLatchedActivationThroughForwardedArgv()
    {
        var forward = Member("App.xaml.cs", "private void ForwardLaunchToPrimary(string pipeName)");

        var build = CSharpSourceText.RequireIndex(
            forward, "ToastActivation.ForwardedArgv(",
            "the forward no longer builds its argv through ForwardedArgv");
        var pending = CSharpSourceText.RequireIndex(
            forward, "ToastActivations.Pending", "the forward no longer reads the latched activation");
        var request = CSharpSourceText.RequireIndex(
            forward, "new Ghostty.Core.SingleInstance.LaunchRequest(",
            "the forward no longer builds a LaunchRequest");

        Assert.True(build < request, "argv must be built before the request that carries it");
        Assert.True(pending < request);
    }

    // A forwarded marker naming a surface that is not here must fall through
    // to an ordinary launch, or the user loses the window (and any jump-list
    // action) they actually asked for.
    [Fact]
    public void ForwardedLaunchChecksLivenessAndFallsThrough()
    {
        var open = Member("App.xaml.cs", "internal void OpenWindowFromLaunch(");

        var liveness = CSharpSourceText.RequireIndex(
            open, "AnyWindowHasToastSurface(",
            "a forwarded activation is honoured without checking it can be");
        var note = CSharpSourceText.RequireIndex(
            open, "ToastActivations.Note(", "a forwarded activation is no longer delivered");
        var fallthrough = CSharpSourceText.RequireIndex(
            open, "HandleJumpListLaunch(",
            "a forwarded launch no longer falls through to an ordinary launch");

        Assert.True(liveness < note, "liveness must be decided before the click is honoured");
        Assert.True(note < fallthrough, "the ordinary launch must be the fall-through, not the first choice");
    }

    // The promise a notification click makes is that the app comes forward.
    // The fallback therefore sits outside the try that wraps the scan, so a
    // throwing scan does not swallow it too.
    [Fact]
    public void ToastConsumerFallsBackToPlainActivationOutsideTheScanTry()
    {
        var consumer = Member("App.xaml.cs", "private void OnToastActivated(");

        var scan = CSharpSourceText.RequireIndex(
            consumer, "TryFocusToastSurface(", "the toast consumer no longer looks for the surface");
        var guard = CSharpSourceText.RequireIndex(
            consumer, "if (focused) return;",
            "the toast consumer no longer separates the scan from its fallback");
        var fallback = CSharpSourceText.RequireIndex(
            consumer, "ShowOrFocusWindowsFromTray()", "the toast consumer no longer falls back at all");

        Assert.True(scan < guard);
        Assert.True(fallback > guard, "the fallback must run after, and outside, the scan's try");
    }

    // Both handles are process-lifetime singletons, so a subscription left
    // attached roots App for the life of the process. Anchored between two
    // neighbours so a deletion cannot pass by leaving the names elsewhere.
    [Fact]
    public void TeardownDetachesBothToastSubscriptions()
    {
        var teardown = Member(
            "App.xaml.cs", "internal void OnAnyWindowClosedInternal(object sender, WindowEventArgs args)");

        var tray = CSharpSourceText.RequireIndex(
            teardown, "_trayIconService = null;", "the teardown no longer drops the tray icon");
        var detach = CSharpSourceText.RequireIndex(
            teardown, "NotificationInvoked -= OnToastNotificationInvoked",
            "the teardown no longer detaches the WinRT toast handler");
        var reset = CSharpSourceText.RequireIndex(
            teardown, "ToastActivations.Reset()", "the teardown no longer resets the relay");
        var host = CSharpSourceText.RequireIndex(
            teardown, "_bootstrapHost?.Dispose()", "the teardown no longer disposes the bootstrap host");

        Assert.True(detach > tray && detach < host, "the detach must sit in the teardown sequence");
        Assert.True(reset > tray && reset < host, "the relay reset must sit in the teardown sequence");
    }

    // Without an argument the activation callback sees only "the app was
    // clicked", never which pane asked for attention.
    [Fact]
    public void ToastCarriesTheSurfaceAsALaunchArgument()
    {
        var show = Member("AppNotificationToastNotifier.cs", "public void Show(ToastRequest request)");

        var body = CSharpSourceText.RequireIndex(
            show, "builder.AddText(request.Body)", "the toast no longer carries its body");
        var argument = CSharpSourceText.RequireIndex(
            show, "builder.AddArgument(", "the toast no longer carries a launch argument");
        var key = CSharpSourceText.RequireIndex(
            show, "ToastActivation.SurfaceArgumentKey",
            "the launch argument is no longer keyed to the surface");
        var build = CSharpSourceText.RequireIndex(
            show, "builder.BuildNotification()", "the toast is no longer built");

        Assert.True(argument > body, "the argument is added to the builder");
        Assert.True(key > argument);
        Assert.True(argument < build, "the argument must be added before the notification is built");
    }

    // The quick terminal's only legal reveal is its own Show(), which
    // re-positions, runs the clip/slide reveal and arms autohide. A bare
    // AppWindow.Show() leaves the last animation frame applied, so the window
    // takes keyboard focus while the user sees nothing.
    [Fact]
    public void QuickTerminalIsRevealedThroughItsOwnShowPath()
    {
        var reveal = Member("MainWindow.xaml.cs", "private void RevealForActivation()");

        var quakeBranch = CSharpSourceText.RequireIndex(
            reveal, "IsQuickTerminal", "the reveal no longer distinguishes the quick terminal");
        var quakeShow = CSharpSourceText.RequireIndex(
            reveal, "Show();", "the quick terminal is no longer revealed through Show()");
        var plainShow = CSharpSourceText.RequireIndex(
            reveal, "AppWindow.Show()", "the ordinary window is no longer shown");

        Assert.True(quakeBranch < quakeShow);
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
        var focus = Member("MainWindow.xaml.cs", "internal bool TryFocusToastSurface(string surfaceKey)");

        var tab = CSharpSourceText.RequireIndex(
            focus, "_tabManager.Activate(", "the toast focus no longer selects the surface's tab");
        var reveal = CSharpSourceText.RequireIndex(
            focus, "RevealForActivation()", "the toast focus no longer reveals the window");
        var defer = CSharpSourceText.RequireIndex(
            focus, "DispatcherQueue.TryEnqueue(",
            "the pane focus is applied inline again, before the visual tree is realized");

        Assert.True(tab < reveal, "the tab is selected before the window is revealed");
        Assert.True(defer > reveal, "the focus is deferred after the reveal, not before it");
    }

    // Something other than the toggle put a window back on screen, so the
    // toggle's bookkeeping has to learn about it or the next press restores
    // instead of hiding.
    [Fact]
    public void RevealingAWindowForAToastUpdatesTheVisibilityToggleBookkeeping()
    {
        var scan = Member("App.xaml.cs", "private bool TryFocusToastSurface(string surfaceKey)");
        CSharpSourceText.RequireIndex(
            scan, "NoteWindowRevealed(", "a toast reveal no longer tells the toggle bookkeeping");

        var note = Member("App.xaml.cs", "private void NoteWindowRevealed(MainWindow window)");
        var remove = CSharpSourceText.RequireIndex(
            note, "_hiddenByVisibilityToggle.Remove(", "the revealed window is not dropped from the hidden set");
        var clear = CSharpSourceText.RequireIndex(
            note, "_windowsHiddenByVisibilityToggle = false", "the hidden flag is never cleared");

        Assert.True(remove < clear, "the flag is cleared only once the set has drained");
    }

    // One window mid-teardown throws out of its own state. Unguarded, it ends
    // the scan before a live window behind it is reached. Both scans walk the
    // same state and both need it.
    [Theory]
    [InlineData("private bool TryFocusToastSurface(string surfaceKey)")]
    [InlineData("private static bool AnyWindowHasToastSurface(string surfaceKey)")]
    public void WindowScansAreGuardedPerWindow(string declaration)
    {
        var scan = Member("App.xaml.cs", declaration);

        var loop = CSharpSourceText.RequireIndex(scan, "foreach (var window", "the scan no longer walks the windows");
        var guard = CSharpSourceText.RequireIndex(
            scan, "catch (System.Exception", "one dead window can end this scan and take the caller with it");
        var close = CSharpSourceText.RequireIndex(scan, "return false;", "the scan no longer reports a miss");

        Assert.True(guard > loop, "the guard must be inside the loop, not around it");
        Assert.True(guard < close, "the guard must be inside the loop, not after it");
    }

    // Enqueued from a pipe thread onto the UI thread, where nothing above
    // catches: an escape takes the process down.
    [Fact]
    public void InboundForwardedLaunchIsGuarded()
    {
        var start = Member("App.xaml.cs", "private void StartSingleInstanceServer()");

        var enqueue = CSharpSourceText.RequireIndex(
            start, "OpenWindowFromLaunch(req)", "the server no longer hands forwarded launches to the app");
        var guard = CSharpSourceText.RequireIndex(
            start, "LogInboundLaunchFailed(",
            "a throwing forwarded launch is an unhandled UI-thread exception again");

        Assert.True(guard > enqueue, "the guard wraps the call");
    }

    private static string OnLaunched()
        => Member("App.xaml.cs", "protected override void OnLaunched(LaunchActivatedEventArgs args)");

    private static string Member(string fileSuffix, string declaration)
        => CSharpSourceText.Member(ReadEmbedded(fileSuffix), declaration);

    private static string Stripped(string fileSuffix)
        => CSharpSourceText.Strip(ReadEmbedded(fileSuffix));

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
