using System;

namespace Ghostty.Services;

/// <summary>
/// The OS animation-effects preference, for the custom motion paths that
/// live outside the tab strips: the bell border fade, the pane startup
/// glow's orbit, and the quick terminal's slide. The strips read
/// <c>UISettings.AnimationsEnabled</c> through their own per-gesture
/// checks (see <c>TabStripMotion</c>); these callers had no read at all,
/// which is the gap this closes -- reduce-motion is a contract that a
/// custom animation honours by cutting to its end state.
/// </summary>
internal static class SystemAnimations
{
    /// <summary>
    /// Whether system animation effects are on. A new UISettings per read,
    /// mirroring what each strip does per gesture: the reads are rare
    /// (bell dismissals, glow starts, quake toggles), and an instance
    /// cached here would have to be created on a UI thread to be trusted
    /// anyway.
    /// </summary>
    public static bool Enabled()
    {
        try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException or NullReferenceException)
        {
            // Unreadable is not "off": fail open, matching the strips'
            // identical guards in packaged/sandboxed contexts.
            return true;
        }
    }
}
