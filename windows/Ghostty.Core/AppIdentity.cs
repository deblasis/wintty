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
}
