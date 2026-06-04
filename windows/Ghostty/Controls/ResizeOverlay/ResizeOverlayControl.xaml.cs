using System;
using Ghostty.Core.ResizeOverlay;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Controls.ResizeOverlay;

/// <summary>
/// The transient grid-dimension pill ("80 x 24") shown over a pane while
/// it is resized. Mirrors macOS Ghostty's SurfaceResizeOverlay.
///
/// The control owns its <see cref="ResizeOverlayState"/> and a one-shot
/// auto-hide timer. The per-pane <see cref="TerminalControl"/> feeds it
/// resize events via <see cref="NotifyResize"/>, passing the current
/// config values (read fresh each time so hot-reload is honored) and an
/// <c>allowShow</c> flag that folds in the time-based guards the view-side
/// owner tracks (startup settle grace, focus-bounce window).
/// </summary>
public sealed partial class ResizeOverlayControl : UserControl
{
    private readonly DispatcherQueueTimer _hideTimer;

    public ResizeOverlayControl()
    {
        State = new ResizeOverlayState();
        InitializeComponent();

        // DispatcherQueueTimer fires on the UI thread, so the Tick handler
        // can update the bound state with no marshalling. Non-repeating: each
        // pulse restarts it, so it only fires once the resizing settles.
        //
        // The timer and its Tick handler live for the lifetime of the
        // control on purpose: we do NOT unwire them on Unloaded. WinUI 3
        // raises Unloaded whenever the visual tree reparents a pane (every
        // split / rebuild), not only on real teardown -- the same gotcha
        // TerminalControl.OnUnloaded documents. Dropping the handler there
        // permanently broke auto-hide for every pre-existing pane after a
        // split: the restarted timer would fire into a detached delegate
        // and the pill stayed stuck on screen. Keeping the wiring is safe:
        // control -> timer -> Tick -> control is a self-contained cycle the
        // GC collects together, a stopped non-repeating timer is not
        // retained by the dispatcher, and a hide still pending when a pane
        // is truly closed simply ticks once (harmlessly setting Visibility
        // on the dead control) and releases.
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += OnHideTick;
    }

    /// <summary>
    /// Observable state bound by the XAML. The control owns the instance;
    /// <see cref="NotifyResize"/> mutates it as resize events arrive.
    /// </summary>
    public ResizeOverlayState State { get; }

    /// <summary>
    /// Record a resize and, when warranted, flash the pill. Always updates
    /// the displayed size; only makes the pill visible when
    /// <paramref name="allowShow"/> is true and the
    /// <see cref="ResizeOverlayState"/> decides this change should pulse
    /// (per <paramref name="mode"/> and first-layout / dedup rules).
    /// </summary>
    /// <param name="cols">Current grid column count.</param>
    /// <param name="rows">Current grid row count.</param>
    /// <param name="mode">The resolved resize-overlay mode.</param>
    /// <param name="position">Where the pill should sit in the pane.</param>
    /// <param name="durationMs">How long the pill stays visible.</param>
    /// <param name="allowShow">
    /// False suppresses the visual pulse (e.g. during the startup settle or
    /// just after a focus change) while still letting the state track the
    /// latest size and baseline.
    /// </param>
    public void NotifyResize(
        ushort cols,
        ushort rows,
        ResizeOverlayMode mode,
        ResizeOverlayPosition position,
        int durationMs,
        bool allowShow)
    {
        State.Mode = mode;

        // The state owns the show/hide decision and flips IsVisible, which the
        // pill's Visibility is bound to. We only handle the view concerns:
        // position and the wall-clock auto-hide timer.
        if (!State.NotifyResize(cols, rows, allowShow)) return;

        ApplyPosition(position);

        // Clamp to a sane floor: a zero/negative duration would make the
        // timer never fire, leaving the pill stuck on screen.
        _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, durationMs));
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnHideTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        State.Hide();
    }

    /// <summary>
    /// x:Bind helper that maps the observable
    /// <see cref="ResizeOverlayState.IsVisible"/> flag to a XAML
    /// <see cref="Visibility"/>. The conversion lives here, in the view layer,
    /// so the Core state stays free of any WinUI types.
    ///
    /// Instance, not static: x:Bind function codegen emits
    /// <c>this.ToVisibility(...)</c>, so a static method fails to compile
    /// (CS0176). Do not "tidy" it to static.
    /// </summary>
    private Visibility ToVisibility(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyPosition(ResizeOverlayPosition position)
    {
        Pill.HorizontalAlignment = position switch
        {
            ResizeOverlayPosition.TopLeft or
            ResizeOverlayPosition.BottomLeft => HorizontalAlignment.Left,
            ResizeOverlayPosition.TopRight or
            ResizeOverlayPosition.BottomRight => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };

        Pill.VerticalAlignment = position switch
        {
            ResizeOverlayPosition.TopLeft or
            ResizeOverlayPosition.TopCenter or
            ResizeOverlayPosition.TopRight => VerticalAlignment.Top,
            ResizeOverlayPosition.BottomLeft or
            ResizeOverlayPosition.BottomCenter or
            ResizeOverlayPosition.BottomRight => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center,
        };
    }
}
