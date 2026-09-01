using System;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Ghostty.Tabs;

/// <summary>
/// The one brush the active tab's field is painted with, and the settle that
/// carries it from the chrome the tab was wearing to the terminal's ground.
/// </summary>
/// <remarks>
/// One instance rather than one colour, because the tab is not the only thing
/// wearing this fill: the seam cover paints the same colour over the strip of
/// pane border the tab joins to, and that is what makes the two read as one
/// surface with no line between them. Two brushes agreeing at rest would
/// still disagree for the length of the transition -- a band of the old
/// chrome sitting under a tab that had already reached the field, which is
/// exactly the join this whole treatment exists to close.
///
/// Shared by both layouts. The horizontal strip installs it as the selected
/// item's header background and hands it to MainWindow's seam cover; the
/// vertical strip paints the selection row with it, and MainWindow's vertical
/// cover reads it straight back off that row.
/// </remarks>
internal sealed class ActiveFieldFill
{
    /// <summary>The brush every painter of the field must use.</summary>
    public SolidColorBrush Brush { get; } = new(Microsoft.UI.Colors.Transparent);

    /// <summary>
    /// Whether anything has told the field what colour it is yet. Callers that
    /// paint a surface OTHER than the tab -- the seam cover -- must not paint
    /// before this is true: transparent over the pane border is the line the
    /// cover exists to hide.
    /// </summary>
    public bool HasColor { get; private set; }

    private Storyboard? _running;
    private object? _tab;

    /// <summary>
    /// Point the field at <paramref name="target"/> for <paramref name="tab"/>.
    /// </summary>
    /// <remarks>
    /// Animates only when the active tab actually moved. The chrome passes
    /// that carry this colour run on far more than activation -- a theme
    /// change, a tab added, a drag, a resize -- and re-running the settle on
    /// each would leave the field permanently mid-flight.
    /// </remarks>
    /// <param name="tab">The active tab, by reference. Movement is identity.</param>
    /// <param name="target">The colour the field rests at.</param>
    /// <param name="from">
    /// The chrome the newly active tab was wearing, which is the strip's own
    /// ground. Starting from it is what makes the tab grow into the field
    /// instead of cutting to it.
    /// </param>
    /// <param name="animate">The strip's motion gate, already composed.</param>
    public void Settle(object? tab, Color target, Color from, bool animate)
    {
        var moved = !ReferenceEquals(_tab, tab);
        _tab = tab;

        // A settle in flight is abandoned rather than blended into: Stop puts
        // the base value back, and the branches below both write it again.
        _running?.Stop();
        _running = null;

        HasColor = true;

        if (!moved || !animate || target == from)
        {
            Brush.Color = target;
            return;
        }

        Brush.Color = from;

        var anim = new ColorAnimation
        {
            From = from,
            To = target,
            Duration = new Duration(
                TimeSpan.FromMilliseconds(TabStripMotion.FieldSettleMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(anim, Brush);
        Storyboard.SetTargetProperty(anim, "Color");

        var board = new Storyboard();
        board.Children.Add(anim);
        board.Completed += (_, _) =>
        {
            // Stop before the write, not after. A finished Storyboard holds
            // its last value over the brush's own, so a plain assignment here
            // would be shadowed and the next reader -- a chrome pass, the
            // seam cover, the morph ghost -- would get the pre-flight colour
            // back the moment anything did stop the clock.
            board.Stop();
            Brush.Color = target;
            _running = null;
        };
        _running = board;
        board.Begin();
    }
}
