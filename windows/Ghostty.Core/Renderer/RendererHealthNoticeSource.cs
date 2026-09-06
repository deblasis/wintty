using System;
using System.Collections.Generic;
using Ghostty.Core.Notifications;

namespace Ghostty.Core.Renderer;

/// <summary>
/// Turns the renderer's per-surface <see cref="RendererHealth"/> reports into
/// one banner that is up for exactly as long as some pane is not painting.
/// </summary>
/// <remarks>
/// <para>
/// Without this the signal is invisible. A lost GPU device leaves the pane
/// showing whatever was on it when the device died, so a frozen terminal looks
/// identical to a terminal with nothing to say, and the only trace is a Zig
/// <c>log.warn</c> in a file nobody opens.
/// </para>
/// <para>
/// Membership rather than a last-one-wins flag, because a device loss is an
/// adapter-wide event: every pane reports unhealthy at once and each reports
/// healthy again as its own rebuild lands. Taking the banner down on the first
/// healthy report would clear it while other panes are still dark. Callers
/// must <see cref="Forget"/> a surface that goes away, or a pane closed while
/// broken leaves the banner up with nothing left to fix it.
/// </para>
/// <para>
/// Unlike <see cref="CustomShaderNoticeSource"/> there is no once-per-session
/// gate. That one describes a static config mistake, where the second telling
/// adds nothing; this describes the live state of the GPU, and a device that
/// dies again an hour later is news again.
/// </para>
/// <para>
/// UI-free and dependency-free so the bookkeeping and the copy unit-test
/// without a WinUI runtime. Not thread-safe: call it on the UI thread like
/// everything else touching <see cref="INotificationService"/>.
/// </para>
/// </remarks>
public sealed class RendererHealthNoticeSource
{
    /// <summary>
    /// One key for every pane, so an adapter-wide loss shows a single banner
    /// rather than one per pane.
    /// </summary>
    public const string DedupKey = "renderer-health";

    // The surfaces that last reported unhealthy. A set rather than a count so
    // a surface repeating itself -- which it does, health is reported per
    // frame-completion, not per change, on some paths -- cannot inflate it.
    private readonly HashSet<nint> _unhealthy = new();

    // The banner currently on screen, kept because Dismiss takes the instance.
    private Notice? _active;

    // Set when the viewer closed the banner themselves. Distinct from _active
    // being null, because the two mean opposite things for what to do next:
    // no banner because the outage ended, versus no banner because they have
    // already read this one and put it away.
    private bool _dismissedByViewer;

    /// <summary>
    /// Record what one surface reported.
    /// </summary>
    /// <returns>
    /// What to do about the banner. Both halves are null when nothing changed,
    /// which is the common case.
    /// </returns>
    public RendererHealthNoticeChange Update(nint surface, RendererHealth health)
    {
        var changed = health == RendererHealth.Unhealthy
            ? _unhealthy.Add(surface)
            : _unhealthy.Remove(surface);

        return changed ? Reconcile() : default;
    }

    /// <summary>
    /// Drop a surface that is going away. Call this when the surface is torn
    /// down, not when it moves between windows.
    /// </summary>
    /// <returns>What to do about the banner.</returns>
    public RendererHealthNoticeChange Forget(nint surface) =>
        _unhealthy.Remove(surface) ? Reconcile() : default;

    private RendererHealthNoticeChange Reconcile()
    {
        if (_unhealthy.Count > 0)
        {
            // One banner per outage. A viewer who closed it is not shown it
            // again while the same panes are still down; the flag clears when
            // they all recover, so the next outage speaks up.
            if (_active is not null || _dismissedByViewer) return default;

            // OnDismiss fires for our own Dismiss as well as for the close X,
            // and only the second one should latch. Comparing against _active
            // tells them apart: the recovery path below always clears _active
            // before handing the notice back, so by the time that dismissal
            // lands this no longer matches.
            Notice? raised = null;
            raised = Build(() =>
            {
                if (ReferenceEquals(_active, raised)) _dismissedByViewer = true;
            });
            _active = raised;
            return new RendererHealthNoticeChange(Show: raised, Dismiss: null);
        }

        _dismissedByViewer = false;
        if (_active is null) return default;
        var stale = _active;
        _active = null;
        return new RendererHealthNoticeChange(Show: null, Dismiss: stale);
    }

    private static Notice Build(Action onDismiss) => new()
    {
        Title = "Graphics device lost",
        Message =
            "The GPU stopped responding and Wintty is trying to rebuild the "
            + "renderer. Affected panes stay frozen until it succeeds, and some "
            + "may not come back at all. This usually follows a graphics driver "
            + "update or crash; if it keeps happening, check for a driver update "
            + "and try clearing custom-shader.",
        // Warning, not Error: the terminal session itself is untouched -- the
        // shell keeps running and the scrollback is intact -- so nothing the
        // user typed is at risk even when a pane never repaints.
        Severity = NoticeSeverity.Warning,
        DedupKey = DedupKey,
        // Fires for the close X as well as our own Dismiss, which is why the
        // flag it sets is checked rather than assumed: without it this source
        // would go on believing a banner the viewer closed is still up, and
        // never raise another one.
        OnDismiss = onDismiss,
        // No actions. Nothing here can hurry a driver along, and the one thing
        // the user might change (custom-shader) is named in the copy rather
        // than given a button that would edit their config for them.
    };
}

/// <summary>
/// What <see cref="RendererHealthNoticeSource"/> wants done about the banner.
/// At most one half is set; both are null when nothing changed.
/// </summary>
public readonly record struct RendererHealthNoticeChange(Notice? Show, Notice? Dismiss);
