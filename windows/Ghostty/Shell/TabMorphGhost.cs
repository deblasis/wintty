using System;
using System.Numerics;
using Ghostty.Core.Tabs;
using Ghostty.Tabs;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Shell;

/// <summary>
/// Stand-in for the active tab while the layout switches.
///
/// The two hosts render the active tab as different controls at different
/// sizes: a TabViewItem roughly 240x32 in the header, a NavigationViewItem
/// on a rail that is 220 wide pinned but only 48 collapsed. Cross-fading
/// the hosts therefore shows the active tab twice, at two sizes, drifting
/// apart. This element is the only active-tab visual on screen during the
/// switch: both real ones are hidden, and it travels from the outgoing
/// rect to the incoming one.
///
/// The ghost paints the active row's real fill, preset color or not. Both
/// real elements are hidden for the flight, so a content-only ghost would
/// drop the tab's chrome for the length of the switch and hand it back on
/// arrival -- the tab would appear to lose its selection while in the air.
///
/// WHY THE BOX IS NOT A WIDTH TWEEN. It used to be: Width and Height as
/// dependent DoubleAnimations, which by definition run on the UI thread
/// and relayout this element every frame. Measured over a switch, the UI
/// thread produced three to thirteen frames -- the terminal's own render
/// owns that thread and does not yield -- so the one element the eye
/// actually tracks was the one element delivered at single-digit frame
/// rates while the cross-fade around it ran smoothly on the compositor.
/// That is the whole difference between a transition that reads as
/// designed and one that reads as cheap.
///
/// So the box is composed instead, out of three independent pieces that
/// all live on the compositor and cost no layout at all:
///
/// - The element is laid out ONCE at the larger of the two rects on each
///   axis, and never resized. The label therefore measures against the
///   width it needs to be readable at, whichever end of the flight that
///   is.
/// - The FILL is a Border scaled from the source box to the destination
///   box about its top-left. A solid rounded rectangle is the one thing
///   that may be scaled without apology: there is no text in it to smear,
///   and the corner radius distorting by a fraction of its four pixels is
///   not a thing anyone can see.
/// - The CONTENT (icon and label) is never scaled -- that is what would
///   smear -- and is instead clipped by an inset that sweeps in lockstep
///   with the fill. Text stays at its natural size and is cut off by the
///   shrinking box rather than squashed into it.
///
/// If the compositor refuses any of that, <see cref="ComposeBox"/> throws
/// and the caller drops the ghost: the cross-fade underneath carries the
/// switch, which reads better than a ghost juddering at single-digit
/// frame rates ever did.
/// </summary>
internal sealed partial class TabMorphGhost : Grid
{
    private readonly TextBlock _label;
    private readonly Border _body;
    private readonly Grid _content;

    /// <summary>Label opacity, animated separately from the ghost body.</summary>
    internal UIElement Label => _label;

    /// <summary>
    /// The corner geometry is the DESTINATION's, not a blend: the eye
    /// follows the ghost to its landing, so it should arrive already in the
    /// shape it hands over to, and the mismatch at departure is masked by
    /// the switch starting around it.
    /// </summary>
    internal TabMorphGhost(
        TabModel tab, Brush? fill, Brush? foreground, CornerRadius cornerRadius)
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransform = new TranslateTransform();

        // The fill lives on its own layer so it can be scaled without the
        // label coming with it. The ghost itself paints nothing.
        _body = new Border
        {
            Background = fill,
            CornerRadius = cornerRadius,
            IsHitTestVisible = false,
        };
        Children.Add(_body);

        _content = new Grid { IsHitTestVisible = false };
        _content.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        _content.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = TabIconElementFactory.Create(tab.TabIcon);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(6, 0, 6, 0);
            SetColumn(icon, 0);
            _content.Children.Add(icon);
        }

        _label = new TextBlock
        {
            Text = tab.EffectiveTitle,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.Clip,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 6, 0),
        };
        if (foreground is not null)
            _label.Foreground = foreground;
        SetColumn(_label, 1);
        _content.Children.Add(_label);
        Children.Add(_content);
    }

    internal TranslateTransform Translate => (TranslateTransform)RenderTransform;

    /// <summary>
    /// Lay the ghost out once, at the larger of the two boxes on each
    /// axis, and hand the visible box to the switch's timeline: the fill
    /// scaled between the two rects, the content clipped to the same box,
    /// all expressions of the clock the travel already rides, so box and
    /// travel cannot disagree about where the flight is. The caller must
    /// call this BEFORE the element is measured for the first time -- the
    /// layout size it sets is the size the label wraps against.
    ///
    /// Throws when composition refuses; the caller drops the ghost and
    /// the cross-fade carries the switch. The dependent Width/Height
    /// tween this used to fall back on is gone with the storyboards -- it
    /// was a relayout per frame on a thread that was measured delivering
    /// single-digit frames per flight, which is not a fallback so much as
    /// a slower way to look broken.
    /// </summary>
    /// <param name="timeline">The switch's clock.</param>
    /// <param name="from">The rect the ghost departs.</param>
    /// <param name="to">The rect it lands on.</param>
    /// <param name="startT">How far the switch already is at staging.</param>
    /// <param name="shapeFraction">Share of the flight the box settles in.</param>
    internal void ComposeBox(
        LayoutSwitchTimeline timeline,
        Windows.Foundation.Size from, Windows.Foundation.Size to,
        double startT, double shapeFraction)
    {
        var box = new Windows.Foundation.Size(
            Math.Max(from.Width, to.Width), Math.Max(from.Height, to.Height));
        Width = box.Width;
        Height = box.Height;
        _body.Width = box.Width;
        _body.Height = box.Height;
        _content.Width = box.Width;
        _content.Height = box.Height;

        if (box.Width <= 0 || box.Height <= 0)
            throw new InvalidOperationException("morph box has no area");

        var bodyVisual = ElementCompositionPreview.GetElementVisual(_body);
        var contentVisual = ElementCompositionPreview.GetElementVisual(_content);

        // Scaled about the top-left: the ghost's Translate already
        // carries its position, so the box must grow and shrink from
        // the origin that position names, not from its middle.
        bodyVisual.CenterPoint = Vector3.Zero;
        var fromScale = new Vector2(
            (float)(from.Width / box.Width), (float)(from.Height / box.Height));
        var toScale = new Vector2(
            (float)(to.Width / box.Width), (float)(to.Height / box.Height));
        bodyVisual.Scale = new Vector3(fromScale, 1f);

        // The content is cut down to the same box rather than scaled
        // into it. Insets are measured from the layout box, so they
        // run from "hide nothing beyond the source rect" to "hide
        // nothing beyond the destination rect".
        var clip = contentVisual.Compositor.CreateInsetClip();
        contentVisual.Clip = clip;

        timeline.GhostBox(
            bodyVisual, clip,
            fromScale, toScale,
            rightFrom: (float)(box.Width - from.Width),
            rightTo: (float)(box.Width - to.Width),
            bottomFrom: (float)(box.Height - from.Height),
            bottomTo: (float)(box.Height - to.Height),
            startT, shapeFraction);
    }

    /// <summary>
    /// Release the compositor animations the box is riding on.
    ///
    /// The ghost is discarded whole when a switch lands or is cancelled,
    /// and nothing else in the window references these two visuals, so
    /// this is belt to that braces rather than a leak being closed. It
    /// exists because the one composition animation this file's neighbour
    /// starts -- the pane reveal's inset sweep -- had to learn the same
    /// lesson against a visual that DOES outlive its switch, and a reader
    /// finding one guarded and the other not would reasonably wonder
    /// which of them was the oversight.
    /// </summary>
    internal void StopBoxAnimations()
    {
        try
        {
            var body = ElementCompositionPreview.GetElementVisual(_body);
            body.StopAnimation(nameof(Visual.Scale));
            var content = ElementCompositionPreview.GetElementVisual(_content);
            if (content.Clip is InsetClip clip)
            {
                clip.StopAnimation(nameof(InsetClip.RightInset));
                clip.StopAnimation(nameof(InsetClip.BottomInset));
            }
            content.Clip = null;
        }
        catch (Exception)
        {
            // Composition is already gone, or the visual is. Either way
            // there is nothing left to stop.
        }
    }

    /// <summary>
    /// Hold the source box while the destination is still unknown: a
    /// ghost staged before its landing container realized has nowhere to
    /// aim, so it sits on the departure rect until one arrives and
    /// <see cref="ComposeBox"/> lays it out properly.
    /// </summary>
    internal void ResizeForFallback(double width, double height)
    {
        Width = width;
        Height = height;
    }
}
