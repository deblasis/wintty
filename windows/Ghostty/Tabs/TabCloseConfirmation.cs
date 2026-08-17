using System.Threading.Tasks;
using Ghostty.Core.Config;
using Ghostty.Core.Tabs;
using Ghostty.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Tabs;

/// <summary>
/// Shared close confirmation for every "close this tab" path.
/// Centralised so <see cref="TabHost"/> and
/// <see cref="VerticalTabHost"/> can't drift. Honors upstream
/// <c>confirm-close-surface</c> (false / true / always).
/// A ContentDialog needs a live <see cref="XamlRoot"/>, which is
/// why this is a helper instead of living in <c>Ghostty.Core</c>.
/// </summary>
internal static class TabCloseConfirmation
{
    public static async Task RequestAsync(
        TabManager manager, TabModel tab, XamlRoot? xamlRoot, DialogTracker dialogs)
    {
        var paneCount = tab.PaneHost.PaneCount;
        var mode = ConfirmCloseSurfaceParser.Parse(
            App.ConfigService?.ConfirmCloseSurface);
        if (ConfirmCloseSurfaceParser.ShouldConfirmTabClose(mode, paneCount)
            && xamlRoot is not null)
        {
            var dlg = new ContentDialog
            {
                Title = "Close tab?",
                Content = paneCount > 1
                    ? $"This tab has {paneCount} panes. Close all of them?"
                    : "Close this tab?",
                PrimaryButtonText = paneCount > 1 ? "Close all" : "Close",
                SecondaryButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = xamlRoot,
            };
            using (dialogs.Track(dlg))
            {
                var res = await dlg.ShowAsync();
                if (res != ContentDialogResult.Primary) return;
            }
        }
        manager.CloseTab(tab);
    }
}
