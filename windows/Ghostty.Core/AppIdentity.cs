namespace Ghostty.Core;

/// <summary>
/// Stable identifiers for the Ghostty Windows application. Shared across
/// the shell (AUMID for SetCurrentProcessExplicitAppUserModelID, jump
/// list, taskbar progress, toast notifications) and tests so nobody has
/// to duplicate the string literal.
/// </summary>
internal static class AppIdentity
{
    /// <summary>
    /// Explicit AppUserModelID for the process. Must be set before any
    /// Shell interop call (jump list, taskbar, toasts) — the Shell
    /// caches the process-to-AUMID association on first use.
    /// </summary>
    public const string AumId = "com.deblasis.ghostty";

    /// <summary>
    /// Brand name for what a user reads: window and tab titles, dialog
    /// captions, the <c>+version</c> header, <see cref="LogTag"/>.
    ///
    /// Not the source for the other "Wintty" literals in the tree, which
    /// track something a rebrand must not move:
    /// <list type="bullet">
    /// <item>paths under <c>%LOCALAPPDATA%</c>, where following the brand
    /// would orphan a user's logs, session and window state with nothing
    /// left to migrate them;</item>
    /// <item>the assembly name, which <c>InternalsVisibleTo</c> and
    /// <c>Process.GetProcessesByName</c> have to match;</item>
    /// <item>kernel object and window class names, which single-instance
    /// and the global hotkey key off.</item>
    /// </list>
    /// A rebrand therefore touches this constant, the brand names written
    /// into prose, and the Win32 version resource in <c>ghostty.rc</c>
    /// that Explorer reads, not every "Wintty" in the tree. XAML surfaces
    /// read it from code-behind, since this type is internal and x:Bind
    /// would need it public.
    /// </summary>
    public const string ProductName = "Wintty";

    /// <summary>
    /// Prefix on the diagnostics we write ourselves, so a line stays
    /// attributable when it lands in a console shared with shell and
    /// libghostty output. Reaches gpu.log through the stderr redirect and
    /// ghostty-crash.log through the fatal-startup message; the
    /// App-level crash.log tags its entries by handler name instead.
    /// Built from <see cref="ProductName"/> so it cannot drift from the
    /// brand.
    /// </summary>
    public const string LogTag = $"[{ProductName}]";
}
