using System.Collections.Frozen;

namespace Ghostty.Core.Shell;

/// <summary>
/// Pure-logic classifier for Windows shell executables. Given an
/// executable path, returns whether the shell is known to speak VT
/// natively (safe to bypass ConPTY), uses the Console API (must use
/// ConPTY), or is unrecognized (fall back to ConPTY + log).
///
/// No I/O: the path is not resolved on disk, no process is spawned,
/// no registry is read.
/// </summary>
public static class ShellDetector
{
    // Lookup uses OrdinalIgnoreCase because Windows filenames are
    // case-insensitive. Keys include the ".exe" suffix so the lookup
    // key is exactly the output of Path.GetFileName -- no suffix
    // stripping, no ambiguity between "pwsh" and "pwsh.exe".
    private static readonly FrozenDictionary<string, ShellCapability> KnownShells =
        new Dictionary<string, ShellCapability>
        {
            // VT-aware: safe to bypass ConPTY.
            ["pwsh.exe"]   = ShellCapability.VtAware,   // PowerShell 7+
            ["wsl.exe"]    = ShellCapability.VtAware,   // WSL launcher (distro owns its PTY)
            ["ssh.exe"]    = ShellCapability.VtAware,   // OpenSSH client
            ["bash.exe"]   = ShellCapability.VtAware,   // Git Bash / MSYS2 / Cygwin / legacy WSL1 stub
            ["nu.exe"]     = ShellCapability.VtAware,   // Nushell
            ["zsh.exe"]    = ShellCapability.VtAware,   // MSYS2 / Cygwin
            ["fish.exe"]   = ShellCapability.VtAware,   // MSYS2 / Cygwin
            ["elvish.exe"] = ShellCapability.VtAware,   // Elvish
            ["xonsh.exe"]  = ShellCapability.VtAware,   // Xonsh

            // Console-API: must use ConPTY.
            ["cmd.exe"]        = ShellCapability.ConsoleApi, // Command Prompt
            ["powershell.exe"] = ShellCapability.ConsoleApi, // Windows PowerShell 5.1
        }
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Classify the given executable path. Safe on null, empty, or
    /// whitespace inputs: returns <see cref="ShellCapability.Unknown"/>
    /// with an empty <see cref="ShellDetectionResult.NormalizedFileName"/>.
    /// </summary>
    /// <param name="exePath">
    /// Executable path as passed to a spawner (e.g. <c>ProcessStartInfo.FileName</c>).
    /// Only the leaf filename is inspected; directory components are ignored.
    /// </param>
    public static ShellDetectionResult Detect(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return new ShellDetectionResult(ShellCapability.Unknown, string.Empty);

        var fileName = Path.GetFileName(exePath);
        if (string.IsNullOrEmpty(fileName))
            return new ShellDetectionResult(ShellCapability.Unknown, string.Empty);

        // Normalize unconditionally so NormalizedFileName is stable
        // regardless of input casing. Callers log this verbatim; cheap
        // cost on a cold path for predictable output.
        var normalized = fileName.ToLowerInvariant();

        return KnownShells.TryGetValue(normalized, out var capability)
            ? new ShellDetectionResult(capability, normalized)
            : new ShellDetectionResult(ShellCapability.Unknown, normalized);
    }
}
