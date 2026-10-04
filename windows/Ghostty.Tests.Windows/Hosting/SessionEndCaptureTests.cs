using System;
using Ghostty.Core.Session;
using Ghostty.Hosting;
using Xunit;

namespace Ghostty.Tests.Windows.Hosting;

/// <summary>
/// The session-end window-proc hook, compiled from the same source the app
/// builds (see the <c>Compile Include</c> in the csproj) and driven with
/// faked messages. Nothing here signs anything out: the HWND is never
/// created, <c>Install</c> is not called, and the capture is a delegate
/// the test owns.
///
/// What is under test is the policy the messages carry. The defect was
/// that nothing handled them at all: Windows ends a session by sending
/// WM_QUERYENDSESSION / WM_ENDSESSION and then killing the process, never
/// WM_CLOSE, so <c>Window.Closed</c> never fired, the clean flag was never
/// set, and <c>window-save-state=default</c> had nothing to restore on the
/// next launch.
/// </summary>
public class SessionEndCaptureTests
{
    private const uint WM_CLOSE = 0x0010;

    [Fact]
    public void The_query_is_answered_and_nothing_is_captured()
    {
        var captures = 0;
        var hook = new SessionEndCapture(() => captures++);

        Assert.Equal(
            SessionEndCapture.SessionEndAction.Allow,
            hook.Dispatch(SessionEndCapture.WM_QUERYENDSESSION, new IntPtr(1)));

        // Windows may still call the whole end off, so a capture here would
        // save a session for an end that never happened.
        Assert.Equal(0, captures);
    }

    [Fact]
    public void A_committed_end_captures_once_and_chains()
    {
        var captures = 0;
        var hook = new SessionEndCapture(() => captures++);

        Assert.Equal(
            SessionEndCapture.SessionEndAction.Capture,
            hook.Dispatch(SessionEndCapture.WM_ENDSESSION, new IntPtr(1)));

        Assert.Equal(1, captures);
    }

    [Fact]
    public void The_cancellation_captures_nothing()
    {
        var captures = 0;
        var hook = new SessionEndCapture(() => captures++);

        Assert.Equal(
            SessionEndCapture.SessionEndAction.None,
            hook.Dispatch(SessionEndCapture.WM_ENDSESSION, IntPtr.Zero));

        Assert.Equal(0, captures);
    }

    [Fact]
    public void An_ordinary_close_is_not_ours()
    {
        var captures = 0;
        var hook = new SessionEndCapture(() => captures++);

        Assert.Equal(
            SessionEndCapture.SessionEndAction.None,
            hook.Dispatch(WM_CLOSE, IntPtr.Zero));
        Assert.Equal(0, captures);
    }

    /// <summary>
    /// The whole shape of the fix in one call: the message arrives, the
    /// capture runs to completion BEFORE the proc returns (Windows gives a
    /// process a short bounded window at session end and then ends it --
    /// there is nothing to await), and what it wrote says clean.
    ///
    /// The save is the app's own, expressed here against the pure session
    /// types: a state built the way <c>SessionManager</c> builds it and
    /// round-tripped through the real serializer, so the flag under test
    /// is the one restore reads back
    /// (<c>SessionGate.ShouldRestore</c>).
    /// </summary>
    [Fact]
    public void A_session_end_saves_the_session_clean_before_returning()
    {
        SessionState? written = null;
        var capturedByTheTimeDispatchReturned = false;

        var hook = new SessionEndCapture(() =>
        {
            var state = new SessionState { CleanShutdown = true };
            state.Windows.Add(new WindowSession
            {
                Tabs =
                {
                    new TabSession
                    {
                        UserTitle = "build",
                        Tree = new LeafDto { ProfileId = "pwsh" },
                    },
                },
            });
            written = SessionSerializer.Deserialize(SessionSerializer.Serialize(state));
            capturedByTheTimeDispatchReturned = true;
        });

        hook.Dispatch(SessionEndCapture.WM_ENDSESSION, new IntPtr(1));

        Assert.True(capturedByTheTimeDispatchReturned);
        Assert.NotNull(written);
        Assert.True(
            written!.CleanShutdown &&
                Core.Session.SessionGate.ShouldRestore(
                    Core.Hosting.WindowSaveState.Default, written.CleanShutdown),
            "a session end must leave a file `default` will restore");
        Assert.Equal("build", written.Windows[0].Tabs[0].UserTitle);
    }

    /// <summary>
    /// The two messages arrive in that order, every time. Capturing on the
    /// query as well would mean two writes per session end, and the second
    /// one would land after the panes are already gone.
    /// </summary>
    [Fact]
    public void Query_then_end_captures_exactly_once()
    {
        var captures = 0;
        var hook = new SessionEndCapture(() => captures++);

        hook.Dispatch(SessionEndCapture.WM_QUERYENDSESSION, new IntPtr(1));
        hook.Dispatch(SessionEndCapture.WM_ENDSESSION, new IntPtr(1));

        Assert.Equal(1, captures);
    }
}