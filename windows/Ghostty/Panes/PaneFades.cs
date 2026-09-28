using System;
using Ghostty.Motion;
using Microsoft.UI.Xaml.Media.Animation;

namespace Ghostty.Panes;

/// <summary>
/// The pane fades' visual half: the opacity board a split and a soft
/// close run on a leaf's control. The split's board fades the new pane
/// in; the soft close's fades it away while the cut it fronts waits.
/// The duration is the tracker's cap and nothing here builds a longer
/// one; a gated-off site never reaches this file, so the pane simply
/// appears (or leaves) at full strength.
/// </summary>
internal static class PaneFades
{
    /// <summary>
    /// Build the fade the given <paramref name="kind"/> names, targeted
    /// at the leaf's control. The split's board runs 0 to 1; the soft
    /// close's runs 1 to 0 and its close finishes off the tree when the
    /// board ends. Eased out, so the tail lands quietly. None has no
    /// board by contract: it is the plain cut, and
    /// <c>StartPaneFade</c> never reaches here with it.
    /// </summary>
    public static Storyboard BuildBoard(PaneFadeKind kind, Controls.TerminalControl target)
    {
        var (from, to) = kind switch
        {
            PaneFadeKind.SplitIn => (0.0, 1.0),
            PaneFadeKind.SoftCloseOut => (1.0, 0.0),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no board for this kind"),
        };
        var fade = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(PaneFadeTracker.FadeMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var board = new Storyboard();
        board.Children.Add(fade);
        return board;
    }
}
