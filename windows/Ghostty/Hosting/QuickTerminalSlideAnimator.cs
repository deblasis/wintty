using System;
using System.Numerics;
using Ghostty.Core.Hosting;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Ghostty.Hosting;

/// <summary>
/// Runs the quake show/hide animation on a window's root content visual
/// using <see cref="Microsoft.UI.Composition"/> (the lifted
/// DirectComposition API). The window is always placed at its final rect
/// first (by the caller); this only translates / fades the *content*, so
/// no swap-chain resize occurs and the SwapChainPanel-bound DX12 terminal
/// rides along on the compositor thread.
///
/// Edge positions (top/bottom/left/right) slide via the element's
/// <c>Translation</c> facade (enabled once with
/// <see cref="ElementCompositionPreview.SetIsTranslationEnabled"/>).
/// Center has no edge to slide from, so it fades opacity 0..1.
///
/// A monotonically increasing token guards completion callbacks: if the
/// user re-toggles mid-animation, the stale batch's Completed handler is
/// ignored so a half-finished hide does not call Hide() after a new show.
/// </summary>
internal sealed class QuickTerminalSlideAnimator
{
    private readonly UIElement _content;
    private readonly Visual _visual;
    private readonly Compositor _compositor;
    private long _token;

    public QuickTerminalSlideAnimator(UIElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        // Translation composes WITH XAML layout (unlike Offset, which
        // replaces it), so animating it never fights the layout pass.
        ElementCompositionPreview.SetIsTranslationEnabled(content, true);
        _visual = ElementCompositionPreview.GetElementVisual(content);
        _compositor = _visual.Compositor;
    }

    /// <summary>Slide / fade the content into view from the docked edge.</summary>
    public void AnimateIn(
        QuickTerminalPosition position,
        double width,
        double height,
        TimeSpan duration,
        Action? onCompleted)
    {
        Run(position, width, height, duration, appearing: true, onCompleted);
    }

    /// <summary>Slide / fade the content out, then invoke onCompleted (Hide()).</summary>
    public void AnimateOut(
        QuickTerminalPosition position,
        double width,
        double height,
        TimeSpan duration,
        Action? onCompleted)
    {
        Run(position, width, height, duration, appearing: false, onCompleted);
    }

    private void Run(
        QuickTerminalPosition position,
        double width,
        double height,
        TimeSpan duration,
        bool appearing,
        Action? onCompleted)
    {
        var token = ++_token;

        // Off-screen translation vector for the docked edge. Center uses
        // opacity instead, so its offset is zero.
        var off = position switch
        {
            QuickTerminalPosition.Top => new Vector3(0f, -(float)height, 0f),
            QuickTerminalPosition.Bottom => new Vector3(0f, (float)height, 0f),
            QuickTerminalPosition.Left => new Vector3(-(float)width, 0f, 0f),
            QuickTerminalPosition.Right => new Vector3((float)width, 0f, 0f),
            _ => Vector3.Zero, // Center
        };
        var isCenter = position == QuickTerminalPosition.Center;

        // Ease-out on appear (decelerate into place), ease-in on hide.
        var easing = appearing
            ? _compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f))
            : _compositor.CreateCubicBezierEasingFunction(new Vector2(0.8f, 0.0f), new Vector2(0.9f, 0.1f));

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        if (isCenter)
        {
            // Fade. Set the start opacity explicitly so a re-toggle from a
            // partial state still animates the full range.
            _visual.Opacity = appearing ? 0f : 1f;
            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, appearing ? 1f : 0f, easing);
            fade.Duration = duration;
            _visual.StartAnimation("Opacity", fade);
        }
        else
        {
            var slide = _compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0f, appearing ? off : Vector3.Zero);
            slide.InsertKeyFrame(1f, appearing ? Vector3.Zero : off, easing);
            slide.Duration = duration;
            _visual.StartAnimation("Translation", slide);
        }

        batch.End();
        batch.Completed += (_, _) =>
        {
            if (token != _token) return; // superseded by a newer toggle
            // Normalize to the resting state so the next show starts clean.
            if (isCenter) _visual.Opacity = appearing ? 1f : 0f;
            onCompleted?.Invoke();
        };
    }

    /// <summary>
    /// Snap to the fully-shown resting state with no animation (used when
    /// duration == 0, or to clear a half-finished animation). Bumps the
    /// token so any in-flight batch completion is ignored.
    /// </summary>
    public void SnapToShown()
    {
        _token++;
        _visual.StopAnimation("Translation");
        _visual.StopAnimation("Opacity");
        _visual.Properties.InsertVector3("Translation", Vector3.Zero);
        _visual.Opacity = 1f;
    }
}
