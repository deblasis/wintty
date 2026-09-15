using System;

namespace Ghostty.Core.Themes;

/// <summary>
/// The command palette theme list's filter, applied once typing pauses
/// rather than on every keystroke.
///
/// Each keystroke replaces the pending filter and restarts the wait, so a
/// word typed quickly refilters the list once, and the preview that follows
/// the highlight (<see cref="PaletteThemeBrowse"/>) is asked for once, for
/// the theme the finished word lands on. A preview rebuilds the terminals'
/// config, and showing a theme for each prefix of a word on the way to the
/// one the user meant is the flicker this exists to prevent.
///
/// Anything that acts on the list first (an arrow key, Page Up or Down,
/// Enter) calls <see cref="Flush"/>, so it acts on the list the typed text
/// describes, never on the one from before the last keystroke. Leaving the
/// theme list calls <see cref="Cancel"/>, so a filter still waiting never
/// lands on whatever the palette shows next.
///
/// Every scheduled callback carries the request it was made for, and one that
/// a later request, a flush or a cancel has overtaken does nothing. Not
/// thread-safe: the palette drives it from the UI thread, and the scheduler
/// it is given calls back on that thread.
/// </summary>
public sealed class PaletteFilterDebounce
{
    /// <summary>
    /// How long typing has to pause before the filter applies: long enough to
    /// cover the gap between the keys of a word typed at speed, short enough
    /// that the list still reads as following the typing.
    /// </summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(120);

    private readonly Action<TimeSpan, Action> _schedule;
    private Action? _pending;
    private int _generation;

    /// <param name="schedule">Runs the action on the UI thread after the delay.</param>
    public PaletteFilterDebounce(Action<TimeSpan, Action> schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        _schedule = schedule;
    }

    /// <summary>Whether a filter is waiting for typing to pause.</summary>
    public bool IsPending => _pending is not null;

    /// <summary>
    /// The text changed: apply <paramref name="apply"/> once typing pauses for
    /// <see cref="Delay"/>, in place of anything still waiting.
    /// </summary>
    public void Request(Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _pending = apply;
        var generation = ++_generation;
        _schedule(Delay, () =>
        {
            if (generation != _generation) return;
            Flush();
        });
    }

    /// <summary>Apply the waiting filter now, if there is one.</summary>
    public void Flush()
    {
        if (_pending is not { } apply) return;
        _pending = null;
        _generation++;
        apply();
    }

    /// <summary>Drop the waiting filter, if there is one, without applying it.</summary>
    public void Cancel()
    {
        _pending = null;
        _generation++;
    }
}
