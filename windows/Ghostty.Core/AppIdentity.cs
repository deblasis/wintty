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
    /// Brand name shown in user-facing surfaces (window titles, dialog
    /// captions). Gated behind a single constant so rebrands (Ghostty →
    /// Wintty → ...) only touch one file instead of scattering literals.
    /// </summary>
    public const string ProductName = "Wintty";

    /// <summary>
    /// Prefix on diagnostics we write to stderr, gpu.log and the crash
    /// log, so a line is attributable at a glance when it lands in a
    /// shared console alongside shell and libghostty output. Built from
    /// <see cref="ProductName"/> so it cannot drift from the brand.
    /// </summary>
    public const string LogTag = $"[{ProductName}]";
}
