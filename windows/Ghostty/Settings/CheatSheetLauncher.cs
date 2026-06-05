using System;
using System.Threading.Tasks;
using Ghostty.Core.Input;
using Ghostty.Interop;
using Ghostty.Logging;
using Ghostty.Services;
using Microsoft.UI.Xaml;

namespace Ghostty.Settings;

/// <summary>
/// Single entry point for the keyboard-shortcuts cheat sheet. Enumerates the
/// current binds, builds the catalog, and shows the dialog. Holds the only
/// re-entrancy guard (WinUI allows one ContentDialog at a time; a second
/// ShowAsync would throw on the caller's fire-and-forget stack).
/// </summary>
internal static class CheatSheetLauncher
{
    private static bool _open;

    public static async Task ShowAsync(ConfigService configService, XamlRoot? root, IntPtr ownerHwnd)
    {
        if (_open || root is null) return;
        _open = true;
        try
        {
            var binds = KeybindEnumerator.Enumerate(configService.ConfigHandle);
            var defaults = configService.EnumerateDefaultKeybinds();
            var catalog = KeybindCatalog.Build(binds, defaults);
            var dialog = new CheatSheetDialog(catalog, ownerHwnd) { XamlRoot = root };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            StaticLoggers.CheatSheet.LogCheatSheetShowFailed(ex);
        }
        finally
        {
            _open = false;
        }
    }
}
