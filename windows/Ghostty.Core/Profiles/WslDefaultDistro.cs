using System;
using Microsoft.Win32;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Reads the user's default WSL distribution from the registry. Cached
/// for the process lifetime — distros change at install time, not at
/// runtime. Returns null if WSL isn't installed or the registry shape
/// changes.
///
/// The default-distro lookup feeds <see cref="WindowsIconResolver"/>'s
/// AutoForWslDistro fallback: when a profile resolves to
/// <c>AutoForWslDistro("")</c> (e.g., the active-shell tracker sees a
/// bare <c>wsl.exe</c> command line with no <c>-d</c> flag), the icon
/// should still show the brand of whichever distro WSL launches.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WslDefaultDistro
{
    // Modern WSL (Store / winget installs and Windows 11 inbox WSL)
    // stores distros under Software\Microsoft\Windows\CurrentVersion\Lxss.
    // Older third-party docs point at the NT\ subpath which is unused
    // on current Windows; stick to the modern path.
    private const string LxssRoot = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    private static string? _cached;
    private static bool _resolved;
    private static readonly object _lock = new();

    public static string? Resolve()
    {
        lock (_lock)
        {
            if (_resolved) return _cached;
            _resolved = true;
            _cached = TryReadFromRegistry();
            return _cached;
        }
    }

    private static string? TryReadFromRegistry()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(LxssRoot);
            if (root is null) return null;

            var defaultGuid = root.GetValue("DefaultDistribution") as string;
            if (string.IsNullOrEmpty(defaultGuid)) return null;

            using var distro = root.OpenSubKey(defaultGuid);
            if (distro is null) return null;

            return distro.GetValue("DistributionName") as string;
        }
        catch
        {
            // Registry access denied / shape changed / WSL uninstalled mid-session.
            // Silent fallback: caller sees null and lands on the legacy wsl.png.
            return null;
        }
    }
}
