using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// Small visual-tree walker used by the context menu handlers to
/// distinguish "clicked a tab" from "clicked empty strip space".
/// </summary>
internal static class VisualTreeHelperEx
{
    public static T? FindAncestor<T>(DependencyObject? start) where T : class
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
