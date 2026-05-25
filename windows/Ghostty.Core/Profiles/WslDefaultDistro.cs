using System;
using Microsoft.Win32;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Reads the user's default WSL distribution from the registry. Successful
/// lookups are cached for the process lifetime because distros change at
/// install time, not at runtime. A null result is intentionally NOT cached:
/// a user who installs WSL after wintty launched would otherwise see the
/// generic wsl.png until app restart, so we retry on the next call instead.
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
    private static readonly object _lock = new();

    public static string? Resolve()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;
            var fresh = TryReadFromRegistry();
            if (fresh is not null) _cached = fresh;
            return fresh;
        }
    }

    private static string? TryReadFromRegistry()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(LxssRoot);
            if (root is null) return null;

            if (root.GetValue("DefaultDistribution") is not string defaultGuid
                || defaultGuid.Length == 0)
            {
                return null;
            }

            using var distro = root.OpenSubKey(defaultGuid);
            if (distro is null) return null;

            return distro.GetValue("DistributionName") as string;
        }
        catch (Exception ex) when (
            ex is System.Security.SecurityException
            or System.IO.IOException
            or UnauthorizedAccessException
            or ObjectDisposedException)
        {
            // Registry access denied, shape changed, or WSL uninstalled
            // mid-session. Silent fallback: caller sees null and lands on
            // the legacy wsl.png. Surfaced to Debug so a regressed
            // resolver is diagnosable from a devenv-attached run without
            // pulling ILogger into a pure-Core helper.
            System.Diagnostics.Debug.WriteLine(
                $"WslDefaultDistro: registry read failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
