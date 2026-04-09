using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Tabs;

/// <summary>
/// Shared multi-pane confirmation dialog for every "close this tab"
/// path. Centralised so <see cref="TabHost"/> and
/// <see cref="VerticalTabHost"/> can't drift. A ContentDialog needs
/// a live <see cref="XamlRoot"/>, which is why this is a helper
/// instead of living in <c>Ghostty.Core</c>.
/// </summary>
internal static class TabCloseConfirmation
{
    public static async Task RequestAsync(TabManager manager, TabModel tab, XamlRoot? xamlRoot)
    {
        // TODO(config): confirm-close-multi-pane (bool, default true)
        const bool confirmCloseMultiPane = true;

        var paneCount = tab.PaneHost.PaneCount;
        if (confirmCloseMultiPane && paneCount > 1 && xamlRoot is not null)
        {
            var dlg = new ContentDialog
            {
                Title = "Close tab?",
                Content = $"This tab has {paneCount} panes. Close all of them?",
                PrimaryButtonText = "Close all",
                SecondaryButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = xamlRoot,
            };
            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;
        }
        manager.CloseTab(tab);
    }
}
