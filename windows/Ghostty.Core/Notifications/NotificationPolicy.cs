namespace Ghostty.Core.Notifications;

/// <summary>
/// Pure decisions about whether (and with what content) a libghostty
/// notification action should raise a Windows toast. No UI or native
/// dependencies, so focus-gating and formatting are unit-tested without
/// WinAppSDK. The shell feeds in the already-decoded payload plus the
/// emitting surface's focus state.
/// </summary>
public static class NotificationPolicy
{
    /// <summary>
    /// OSC 9 / OSC 777 desktop notification. Suppressed when the emitting
    /// surface is active -- i.e. focused AND in the foreground window (the
    /// user is already looking at it; mirrors macOS's requireFocus default).
    /// The core already enforced the `desktop-notifications` config before
    /// dispatching, so there is no config check here. Returns null when
    /// nothing should be shown.
    /// </summary>
    public static ToastRequest? DesktopNotification(
        string title,
        string body,
        string surfaceKey,
        bool isSurfaceActive)
    {
        if (isSurfaceActive) return null;
        return new ToastRequest(title, body, surfaceKey);
    }

    /// <summary>
    /// The shell process exited. Suppressed when the surface is active
    /// (focused + foreground), and when <paramref name="runtimeMs"/> is 0.
    /// The zero-runtime check is a Windows-specific launch-failure guard
    /// (rules out exit codes reported the instant a surface spins up); the
    /// macOS port instead classifies abnormal exits via a runtime threshold.
    /// The toast is additive: the core still prints its in-terminal "Press
    /// any key to close" message because the apprt returns "not handled".
    /// </summary>
    public static ToastRequest? ChildExited(
        uint exitCode,
        ulong runtimeMs,
        string surfaceKey,
        bool isSurfaceActive)
    {
        if (runtimeMs == 0) return null;
        if (isSurfaceActive) return null;

        var title = exitCode == 0 ? "Process exited" : "Process exited abnormally";
        var body = exitCode == 0
            ? "The shell exited normally (code 0)."
            : $"The shell exited with code {exitCode}.";
        return new ToastRequest(title, body, surfaceKey);
    }
}
