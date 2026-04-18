using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Registers the <c>wintty://</c> URI scheme in HKCU so toast-click
/// and jump-list activations route back to the running exe. Safe to
/// call on every startup; re-registration is idempotent. HKCU means
/// no elevation needed for unpackaged installs.
/// </summary>
internal static class WinttyProtocolRegistrar
{
    private const string KeyPath = @"SOFTWARE\Classes\wintty";

    public static void EnsureRegistered(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key!.SetValue(string.Empty, "URL:Wintty Terminal");
            key.SetValue("URL Protocol", string.Empty);
            using var iconKey = key.CreateSubKey("DefaultIcon");
            iconKey!.SetValue(string.Empty, $"\"{exePath}\",0");
            using var commandKey = key.CreateSubKey(@"shell\open\command");
            commandKey!.SetValue(string.Empty, $"\"{exePath}\" --uri \"%1\"");
            Debug.WriteLine("[sponsor/update] wintty:// scheme registered in HKCU");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] scheme register failed: {ex.Message}");
        }
    }
}
