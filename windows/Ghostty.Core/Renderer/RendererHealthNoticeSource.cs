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

    // The surfaces the renderer has given up on. Held apart from _unhealthy
    // rather than folded in, because the two need opposite advice: one is
    // "wait", the other is "this pane is not coming back". The Update arms
    // keep them disjoint, so a pane is in at most one.
    private readonly HashSet<nint> _abandoned = new();

    // The banner currently on screen, kept because Dismiss takes the instance.
    private Notice? _active;

    // Which of the two banners _active is, so a pane being given up on while
    // the "rebuilding" banner is up replaces it rather than being swallowed.
    private bool _activeIsAbandoned;

    // How bad the news is. Ordered, because what the viewer has already been
    // told is only worth repeating when it gets worse.
    private enum Level
    {
        Rebuilding,
        Abandoned,
    }

    // The most severe banner the viewer has closed during this outage, or
    // null if they have closed none. A dismissal means "I have seen enough
    // about this outage", so this level and anything below it stays quiet;
    // only something worse is worth raising again. Cleared when every pane
    // recovers, so the next outage speaks up.
    private Level? _dismissed;

    /// <summary>
    /// Record what one surface reported.
    /// </summary>
    /// <returns>
    /// What to do about the banner. Both halves are null when nothing changed,
    /// which is the common case.
    /// </returns>
    public RendererHealthNoticeChange Update(nint surface, RendererHealth health)
    {
        var changed = health switch
        {
            // Abandoned is terminal, so the pane moves out of the set that
            // still expects a recovery.
            RendererHealth.Abandoned => _abandoned.Add(surface) | _unhealthy.Remove(surface),
            // Removing from _abandoned keeps the sets disjoint by
            // construction rather than by trusting the renderer never to
            // walk that transition back.
            RendererHealth.Unhealthy => _unhealthy.Add(surface) | _abandoned.Remove(surface),
            _ => _unhealthy.Remove(surface) | _abandoned.Remove(surface),
        };

        return changed ? Reconcile() : default;
    }

    /// <summary>
    /// Drop a surface that is going away. Call this when the surface is torn
    /// down, not when it moves between windows.
    /// </summary>
    /// <returns>What to do about the banner.</returns>
    public RendererHealthNoticeChange Forget(nint surface) =>
        _unhealthy.Remove(surface) | _abandoned.Remove(surface) ? Reconcile() : default;

    private RendererHealthNoticeChange Reconcile()
    {
        // A pane nothing will rebuild outranks one still being retried: its
        // advice is the only advice that helps, and it is the only pane the
        // user has to act on themselves.
        var wantAbandoned = _abandoned.Count > 0;
        var want = wantAbandoned || _unhealthy.Count > 0;

        if (want)
        {
            var level = wantAbandoned ? Level.Abandoned : Level.Rebuilding;

            // Already saying the right thing.
            if (_active is not null && _activeIsAbandoned == wantAbandoned) return default;

            // The viewer has already been told this much. Take down whatever
            // is up if it now says the wrong thing, but do not raise it again.
            if (_dismissed is { } seen && seen >= level)
            {
                return Retract();
            }

            // Changing level replaces the banner. Both share a DedupKey, so
            // the old one has to come down first or Show is a no-op -- which
            // is why the caller applies Dismiss before Show.
            var stale = _active;

            // OnDismiss fires for our own Dismiss as well as for the close X,
            // and only the second one should latch. Comparing against _active
            // tells them apart: we always clear or replace _active before
            // handing a notice back to be dismissed, so by the time that
            // dismissal lands this no longer matches.
            Notice? raised = null;
            raised = Build(wantAbandoned, () =>
            {
                if (!ReferenceEquals(_active, raised)) return;
                _dismissed = level;
            });
            _active = raised;
            _activeIsAbandoned = wantAbandoned;
            return new RendererHealthNoticeChange(Show: raised, Dismiss: stale);
        }

        // The outage is over, so the next one starts from a clean slate.
        _dismissed = null;
        return Retract();
    }

    // Take the banner down, whatever it was saying, and report that.
    private RendererHealthNoticeChange Retract()
    {
        if (_active is null) return default;
        var last = _active;
        _active = null;
        _activeIsAbandoned = false;
        return new RendererHealthNoticeChange(Show: null, Dismiss: last);
    }

    private static Notice Build(bool abandoned, Action onDismiss) => new()
    {
        Title = abandoned ? "Graphics device gave out" : "Graphics device lost",
        Message = abandoned
            // The only case where the user has something to do, and the only
            // one where waiting is the wrong advice.
            // The reason does not cross the ABI, only the state does, so this
            // cannot say which of the three reasons applied. The renderer
            // blames a custom-shader for exactly one of them and refuses to
            // for the other two, so this offers it as something to try rather
            // than as the likely cause; the log line names it when it is.
            ? "Wintty has stopped trying to rebuild the renderer for one or more "
              + "panes, so those will not come back on their own. Open a new tab "
              + "to carry on; the shell in the frozen pane is still running and "
              + "its scrollback is intact. If you use a custom-shader, it is "
              + "worth checking whether this still happens without it."
            : "The GPU stopped responding and Wintty is trying to rebuild the "
              + "renderer. Affected panes stay frozen until it succeeds. This "
              + "usually follows a graphics driver update or crash; if it keeps "
              + "happening, check for a driver update and try clearing "
              + "custom-shader.",
        // Warning, not Error, in both cases: the terminal session itself is
        // untouched -- the shell keeps running and the scrollback is intact --
        // so nothing the user typed is at risk even when a pane never repaints.
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
/// Both are null when nothing changed. Either half can be set on its own, and
/// BOTH are set when one banner replaces another — callers must apply
/// <see cref="Dismiss"/> before <see cref="Show"/>, because the two share a
/// <see cref="Notice.DedupKey"/> and showing first makes the replacement a
/// no-op. Every call site has to handle both halves; one that applies only
/// the dismissal drops a banner other panes still need.
/// </summary>
public readonly record struct RendererHealthNoticeChange(Notice? Show, Notice? Dismiss);
