using System;
using System.Numerics;
using Ghostty.Controls.Settings;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Settings;

/// <summary>
/// Helpers used by the search results pane to scroll a target card
/// into view and pulse it briefly. Keeps the visual-tree walk and the
/// composition animation in one place so SettingsWindow stays focused
/// on routing.
/// </summary>
internal static class SettingsCardLocator
{
    public static SettingsCard? FindByConfigKey(DependencyObject root, string key)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is SettingsCard card && SettingsCard.GetConfigKey(card) == key)
                return card;
            var nested = FindByConfigKey(child, key);
            if (nested != null) return nested;
        }
        return null;
    }

    public static void ScrollIntoView(FrameworkElement target)
    {
        // Walk ancestors to find the enclosing ScrollViewer so we
        // translate the target's bounds into scroll-viewer coordinates.
        var scroller = FindAncestor<ScrollViewer>(target);
        if (scroller == null) return;

        var transform = target.TransformToVisual(scroller);
        var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        // Leave a small top margin so the pulsed card isn't flush
        // against the ScrollViewer edge.
        double offset = Math.Max(0, scroller.VerticalOffset + point.Y - 24);
        scroller.ChangeView(null, offset, null, disableAnimation: false);
    }

    /// <summary>
    /// Brief accent-tinted scale+glow pulse. Uses composition animations
    /// so it runs on the compositor and doesn't fight layout. The card
    /// is left untouched afterward.
    /// </summary>
    public static void Pulse(FrameworkElement target)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        var compositor = visual.Compositor;

        // Center the scale on the card so it grows outward evenly.
        target.UpdateLayout();
        visual.CenterPoint = new Vector3(
            (float)target.ActualWidth / 2f,
            (float)target.ActualHeight / 2f,
            0f);

        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.Duration = TimeSpan.FromMilliseconds(520);
        pulse.InsertKeyFrame(0.0f, new Vector3(1.0f, 1.0f, 1.0f));
        pulse.InsertKeyFrame(0.35f, new Vector3(1.02f, 1.02f, 1.0f));
        pulse.InsertKeyFrame(1.0f, new Vector3(1.0f, 1.0f, 1.0f));

        // Wrap in a scoped batch so we can release the compositor's hold
        // on the Scale property once the keyframes finish. Without the
        // StopAnimation, setting visual.Scale from code later would be
        // silently ignored -- the expression system keeps ownership.
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Scale", pulse);
        batch.End();
        batch.Completed += (_, _) => visual.StopAnimation("Scale");
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : class
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
