using System;
using System.Diagnostics;
using Ghostty.Core.Hosting;
using Ghostty.Interop;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Hosting;

/// <summary>
/// Animates the quake window's show/hide as a window-region "reveal": the
/// window stays at its final rect the whole time and we grow (show) or shrink
/// (hide) its visible region from the docked edge via <c>SetWindowRgn</c>. The
/// area outside the region is not part of the window, so the desktop shows
/// through it -- there is no empty backdrop frame, which is what translating
/// the content inside a full-size window unavoidably produced (the window's own
/// backdrop filled the not-yet-covered area).
///
/// This mirrors Windows Terminal's quake dropdown
/// (<c>IslandWindow::_doSlideAnimation</c>), which clips the window region
/// rather than moving the window or its content. Content never moves and is
/// always fully painted (the DirectComposition surface-handle binding ensures
/// the surface composites on show), so the reveal exposes a live terminal.
///
/// Center has no edge to reveal from, so it fades opacity instead.
///
/// Driven per frame from <see cref="CompositionTarget.Rendering"/> with an
/// ease-out curve. A monotonic token guards completion so a re-toggle mid
/// animation does not fire a stale <c>Hide()</c>.
/// </summary>
internal sealed class QuickTerminalSlideAnimator : IDisposable
{
    private readonly nint _hwnd;
    private readonly Visual _visual; // content visual, used only for the Center fade
    private readonly Stopwatch _clock = new();

    // Monotonic guard: bumped on every Prepare/Run/Snap/Dispose so an in-flight
    // Rendering tick or completion from a superseded run is ignored.
    private long _token;
    private bool _subscribed;

    // Current run parameters.
    private QuickTerminalPosition _position;
    private int _w;
    private int _h;
    private double _durationMs;
    private bool _appearing;
    private Action? _onCompleted;
    private long _runToken;

    public QuickTerminalSlideAnimator(nint hwnd, UIElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _hwnd = hwnd;
        _visual = ElementCompositionPreview.GetElementVisual(content);
    }

    /// <summary>
    /// Synchronously set the collapsed start state BEFORE the window is shown,
    /// so the first presented frame is the reveal's frame 0 (nothing visible)
    /// rather than the full window flashing in.
    /// </summary>
    public void PrepareIn(QuickTerminalPosition position, double width, double height)
    {
        _token++;
        StopLoop();
        if (position == QuickTerminalPosition.Center)
        {
            ClearRegion();
            _visual.Opacity = 0f;
        }
        else
        {
            _visual.Opacity = 1f;
            ApplyRegion(position, (int)width, (int)height, 0.0);
        }
    }

    /// <summary>Reveal the window from its docked edge.</summary>
    public void AnimateIn(QuickTerminalPosition position, double width, double height, TimeSpan duration, Action? onCompleted)
        => Run(position, width, height, duration, appearing: true, onCompleted);

    /// <summary>Collapse the window toward its docked edge, then invoke onCompleted (Hide()).</summary>
    public void AnimateOut(QuickTerminalPosition position, double width, double height, TimeSpan duration, Action? onCompleted)
        => Run(position, width, height, duration, appearing: false, onCompleted);

    private void Run(QuickTerminalPosition position, double width, double height, TimeSpan duration, bool appearing, Action? onCompleted)
    {
        var token = ++_token;
        _runToken = token;
        _position = position;
        _w = (int)width;
        _h = (int)height;
        _durationMs = Math.Max(1.0, duration.TotalMilliseconds);
        _appearing = appearing;
        _onCompleted = onCompleted;

        // Seed frame 0 so the first tick has no visible jump.
        if (position == QuickTerminalPosition.Center)
            _visual.Opacity = appearing ? 0f : 1f;
        else
            ApplyRegion(position, _w, _h, appearing ? 0.0 : 1.0);

        _clock.Restart();
        StartLoop();
    }

    private void OnRendering(object? sender, object e)
    {
        if (_runToken != _token) { StopLoop(); return; }

        var p = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _durationMs, 0.0, 1.0);
        // Ease-out (decelerate into place). visibleFraction: 0->1 appearing, 1->0 hiding.
        var eased = 1.0 - Math.Pow(1.0 - p, 3.0);
        var visible = _appearing ? eased : 1.0 - eased;

        if (_position == QuickTerminalPosition.Center)
            _visual.Opacity = (float)visible;
        else
            ApplyRegion(_position, _w, _h, visible);

        if (p < 1.0) return;

        // Done. Snapshot the run's state before invoking the completion
        // callback -- done() (e.g. AppWindow.Hide()) could synchronously start
        // a new run, which would overwrite _appearing/_position mid-method.
        StopLoop();
        var appearing = _appearing;
        var position = _position;

        if (appearing)
        {
            // Settled: drop the clip so the window is fully rectangular again.
            if (position == QuickTerminalPosition.Center) _visual.Opacity = 1f;
            else ClearRegion();
        }

        var done = _onCompleted;
        _onCompleted = null;
        done?.Invoke();

        // After a hide completes the window is collapsed; reset the clip so the
        // next show starts from a clean (full) window before PrepareIn re-seeds.
        if (!appearing && position != QuickTerminalPosition.Center)
            ClearRegion();
    }

    /// <summary>Snap to the fully-shown rectangular state (duration == 0, or to
    /// clear a half-finished animation).</summary>
    public void SnapToShown()
    {
        _token++;
        StopLoop();
        ClearRegion();
        _visual.Opacity = 1f;
    }

    public void Dispose()
    {
        _token++;
        StopLoop();
        ClearRegion();
        _visual.Opacity = 1f;
    }

    private void StartLoop()
    {
        if (_subscribed) return;
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    private void StopLoop()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
        _clock.Stop();
    }

    // Clip the window to the revealed fraction (0..1) measured from the docked
    // edge. The window stays put; only its visible region grows/shrinks.
    private void ApplyRegion(QuickTerminalPosition position, int w, int h, double f)
    {
        if (w <= 0 || h <= 0) return;
        var fw = Math.Clamp((int)Math.Round(f * w), 0, w);
        var fh = Math.Clamp((int)Math.Round(f * h), 0, h);
        var rgn = position switch
        {
            QuickTerminalPosition.Top => Win32Interop.CreateRectRgn(0, 0, w, fh),
            QuickTerminalPosition.Bottom => Win32Interop.CreateRectRgn(0, h - fh, w, h),
            QuickTerminalPosition.Left => Win32Interop.CreateRectRgn(0, 0, fw, h),
            QuickTerminalPosition.Right => Win32Interop.CreateRectRgn(w - fw, 0, w, h),
            _ => Win32Interop.CreateRectRgn(0, 0, w, h),
        };
        // Bail if region creation failed (e.g. GDI exhaustion): passing a null
        // region to SetWindowRgn would REMOVE the clip (full-window flash) --
        // worse than skipping this frame and retrying on the next tick.
        if (rgn == IntPtr.Zero) return;
        // On success the system takes ownership of rgn (and frees the previous
        // one), so we only delete it ourselves when the call fails.
        if (Win32Interop.SetWindowRgn(_hwnd, rgn, true) == 0)
            Win32Interop.DeleteObject(rgn);
    }

    // Null region == remove the clip (full rectangular window).
    private void ClearRegion() => Win32Interop.SetWindowRgn(_hwnd, IntPtr.Zero, true);
}
