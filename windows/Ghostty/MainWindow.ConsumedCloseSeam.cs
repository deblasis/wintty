#if TESTSEAM
using System.Threading.Tasks;
using Ghostty.Panes;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty;

/// <summary>
/// The test seam's handles on the surfaces the consumed-close scenarios
/// drive (seam-consumed-close-key.ps1). Seam builds only.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The overview chord's own open.</summary>
    internal void TestSeamShowOverview() => ShowTabOverview();

    /// <summary>The notice dock, whose bar keys the seam drives.</summary>
    internal Controls.Notifications.NotificationHost TestSeamNotificationHost => NotificationHost;

    /// <summary>
    /// The active pane's context menu, built and shown the way a right click
    /// shows it, for the seam to dismiss.
    /// </summary>
    internal MenuFlyout TestSeamOpenPaneMenu()
    {
        var paneHost = (Panes.PaneHost)_tabManager.ActiveTab.PaneHost;
        var control = paneHost.ActiveLeaf.Terminal();
        var flyout = BuildPaneContextMenu(control, paneHost);
        flyout.ShowAt(control);
        return flyout;
    }

    /// <summary>The tab rename dialog, opened the way prompt_title opens it.</summary>
    internal Task TestSeamPromptTabTitle() =>
        ShowPromptTitleDialogAsync(isTab: true, _tabManager.ActiveTab.PaneHost.ActiveLeaf.Terminal());
}
#endif
