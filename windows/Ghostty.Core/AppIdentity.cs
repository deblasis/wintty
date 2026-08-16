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
    ///
    /// This is the Shell's identity key, not a display string. Changing
    /// it detaches existing pinned taskbar entries, jump lists and
    /// already-scheduled toasts from the app, and Windows offers no
    /// migration for that, so treat a change as a one-off break rather
    /// than a rename that follows the brand.
    ///
    /// Baked in at build time rather than written here, because every
    /// tier and variant needs its own: the Shell merges installs that
    /// share an AUMID into one taskbar button whatever their pack id
    /// says. The public build gets the default below; wintty-release
    /// overrides <c>_WinttyAumId</c> per variant. It stays a
    /// <see langword="const"/> so call sites can keep using it in
    /// constant contexts and nothing reflects at startup.
    /// </summary>
    public const string AumId = Ghostty.Core.Version.BuildInfo.AumId;

    /// <summary>
    /// Brand name for what a user reads: window and tab titles, dialog
    /// captions, the <c>+version</c> header, <see cref="LogTag"/>. It is
    /// not the source for the other "Wintty" literals in the tree, and
    /// those split two ways.
    ///
    /// Following the brand, and already moved with it: the Win32 version
    /// resource in <c>ghostty.rc</c> that Explorer reads; the config file,
    /// now <c>%APPDATA%\wintty\config.wintty</c>, still falling back to
    /// <c>ghostty\config.ghostty</c> and to the Ghostty &lt;1.3.0
    /// <c>ghostty\config</c> so an existing install keeps working; themes
    /// under <c>%APPDATA%\wintty</c>; and libghostty's crash, sentry, ssh
    /// terminfo cache and WSL terminfo cache directories under
    /// <c>%LOCALAPPDATA%\wintty</c>, which moved with no fallback because
    /// that data regenerates.
    ///
    /// Not following the brand, and a rebrand must not move them:
    /// <list type="bullet">
    /// <item>the shell's own <c>%APPDATA%\Wintty</c> (session, window
    /// state, command frecency) and <c>%LOCALAPPDATA%\Wintty</c> (logs,
    /// crash.log, gpu.log, icon cache), which read <see cref="StateDirName"/>
    /// rather than this constant. Unlike the libghostty caches above this
    /// is state the user would notice losing, with nothing left to migrate
    /// it;</item>
    /// <item>the assembly name, which <c>InternalsVisibleTo</c> and
    /// <c>Process.GetProcessesByName</c> have to match;</item>
    /// <item>kernel object and window class names, which single-instance
    /// and the global hotkey key off;</item>
    /// <item><see cref="AumId"/>, which the Shell keys taskbar pins and
    /// toasts off. See its own remarks.</item>
    /// </list>
    /// XAML surfaces read this constant from code-behind, since this type
    /// is internal and x:Bind would need it public.
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

    /// <summary>
    /// Folder name for this build's per-user state, under both
    /// <c>LocalApplicationData</c> (logs, crash log, caches) and
    /// <c>ApplicationData</c> (session restore).
    ///
    /// Separate from <see cref="ProductName"/> even though the two agree
    /// here: one is a display string and the other is a path component,
    /// and they stop agreeing as soon as a build wants a distinct state
    /// directory without renaming itself on screen.
    /// </summary>
    public const string StateDirName = "Wintty";
}
