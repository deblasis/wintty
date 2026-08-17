using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;

namespace Ghostty.Tabs;

/// <summary>
/// Shared MainWindow lookups for tab detach / snap-zone actions.
/// Both <see cref="TabHost"/> and <see cref="VerticalTabHost"/>
/// resolve the owner the same way: <see cref="App.WindowsByRoot"/>
/// keyed by <see cref="XamlRoot"/>.
/// </summary>
internal static class TabWindowActions
{
    public static void DetachToNewWindow(XamlRoot? root, TabModel tab)
    {
        if (root is not null && App.WindowsByRoot.TryGetValue(root, out var main))
            main.DetachTabToNewWindow(tab);
    }

    public static SnapZoneSource GetSnapSource(XamlRoot? root)
    {
        if (root is not null && App.WindowsByRoot.TryGetValue(root, out var main))
        {
            var display = SnapPlacement.ResolveDisplayFor(main.AppWindow);
            var w = display.WorkArea;
            return new SnapZoneSource(w.Width, w.Height);
        }
        return new SnapZoneSource(1920, 1080);
    }

    public static void DetachWithZone(XamlRoot? root, TabModel tab, SnapZone zone)
    {
        if (root is not null && App.WindowsByRoot.TryGetValue(root, out var main))
            main.DetachTabToZone(tab, zone);
    }
}
