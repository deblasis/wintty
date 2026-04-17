namespace Ghostty.Core.Shell;

/// <summary>
/// VT/Console-API capability verdict for a Windows shell executable.
/// Detection is a case-insensitive filename heuristic; see <see cref="ShellDetector"/>.
/// </summary>
public enum ShellCapability
{
    /// <summary>
    /// Binary is not on the known-shell list. Callers must fall back to
    /// ConPTY (the safe default) and are expected to log for troubleshooting.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Shell speaks VT natively and is safe to spawn over raw pipes
    /// (ConPTY bypass). Examples: pwsh, wsl, ssh, bash, nu.
    /// </summary>
    VtAware,

    /// <summary>
    /// Shell uses the Win32 Console API and must be hosted by ConPTY.
    /// Examples: cmd.exe, Windows PowerShell 5.1 (powershell.exe).
    /// </summary>
    ConsoleApi,
}
