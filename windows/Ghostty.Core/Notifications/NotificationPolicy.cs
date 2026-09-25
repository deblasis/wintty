using System;
using Ghostty.Core.Profiles;

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
    // The child-exit toast names what ran, and that is all it names: a
    // command line can carry tokens and passwords, and toast text persists
    // in the Action Center history long after the terminal is gone
    // (deblasis/wintty#1193). So the subject is the command's PROGRAM,
    // never its arguments, and even the program is capped -- the ellipsis
    // is the visible truncation indicator, so a long name is never
    // silently shortened. The one documented exception is the WSL launch,
    // whose subject names the distro ("WSL: Ubuntu-24.04"): the same words
    // the tab tooltip uses, plain-text-gated and capped like every other
    // subject.
    private const int MaxProgramChars = 80;
    private const string TruncationIndicator = "…";

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
    ///
    /// <paramref name="command"/> is the text the surface was created to
    /// run (an argv keeps its <c>direct:</c> marker), or null for a plain
    /// shell pane. The toast names its program -- "cargo", "PowerShell" --
    /// and the exit code, and never the arguments: that is the pinned
    /// secrets policy for this toast (deblasis/wintty#1193). Without a
    /// command (or a readable first token) the subject falls back to
    /// "The shell", which keeps the copy this toast had before it named
    /// anything.
    /// </summary>
    public static ToastRequest? ChildExited(
        uint exitCode,
        ulong runtimeMs,
        string? command,
        string surfaceKey,
        bool isSurfaceActive)
    {
        if (runtimeMs == 0) return null;
        if (isSurfaceActive) return null;

        var title = exitCode == 0 ? "Process exited" : "Process exited abnormally";
        var subject = Subject(command);
        var body = exitCode == 0
            ? $"{subject} exited normally (code 0)."
            : $"{subject} exited with code {exitCode}.";
        return new ToastRequest(title, body, surfaceKey);
    }

    /// <summary>
    /// What the toast calls what ran: the display name the tab vocabulary
    /// uses ("PowerShell", "cargo", "vim"), the raw basename when it has no
    /// entry, capped at <see cref="MaxProgramChars"/> with the truncation
    /// indicator; "The shell" when there is nothing to name.
    /// </summary>
    private static string Subject(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "The shell";

        // SurfaceCommand leads an argv with the in-band marker libghostty
        // consumes; the program is the first token of what follows it,
        // not the marker itself.
        var text = command.StartsWith(PaneCommandPolicy.SurfaceArgvPrefix, StringComparison.Ordinal)
            ? command[PaneCommandPolicy.SurfaceArgvPrefix.Length..]
            : command;

        var basename = ProfileOrderResolver.CommandBasename(text);
        if (basename is null) return "The shell";

        var display = ProcessDisplayName.For(basename, text);
        if (display.Length <= MaxProgramChars) return display;

        // Back the cut off rather than split a UTF-16 surrogate pair: a
        // lone surrogate is not a legal XML character and the toast sink
        // would drop the whole notification over it.
        var cut = MaxProgramChars;
        if (char.IsHighSurrogate(display[cut - 1])) cut--;
        return display[..cut] + TruncationIndicator;
    }
}
