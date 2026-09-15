using System;

namespace Ghostty.Core.Themes;

/// <summary>
/// What a palette theme browse drives. The shell implements it over its
/// config service; the tests implement it over plain fields, which is what
/// lets every rule below be checked without a window.
/// </summary>
public interface IThemePreviewTarget
{
    /// <summary>The live chrome colours, as a snapshot a cancel can restore.</summary>
    ThemePreviewColors CaptureColors();

    /// <summary>
    /// Put <paramref name="themeName"/> on every live view, terminal and chrome,
    /// without writing anything to the config file. False when the theme could
    /// not be applied (missing file, a name the config cannot carry, teardown),
    /// in which case nothing changed.
    /// </summary>
    bool ApplyPreview(string themeName);

    /// <summary>
    /// Undo every preview: put the committed config back on the live views and,
    /// when <paramref name="colors"/> is not null, those exact chrome colours.
    /// Null means another browse already spent or emptied the shared snapshot,
    /// so the chrome colours are not this browse's to touch.
    /// </summary>
    void Revert(ThemePreviewColors? colors);

    /// <summary>
    /// Persist <paramref name="themeName"/> as the configured theme through the
    /// app's config write path, and reload from it. Called once per confirm.
    /// </summary>
    void Commit(string themeName);
}

/// <summary>
/// One command palette theme browse: the highlighted theme goes onto the live
/// views as the selection moves, Escape puts back exactly what was there, and
/// Enter writes the choice to the config once.
///
/// The chrome snapshot is not kept here. It goes into the process-wide
/// <see cref="InlineThemePreviewSession"/>, the same one slot the inline
/// +list-themes picker and the preview pipe use, so a palette browse that
/// overlaps one of those restores the colours from before either of them
/// started rather than a preview the other one put up. The terminal side needs
/// no snapshot at all: a preview never replaces the committed config, it only
/// shows another one, so reverting is showing the committed one again.
///
/// Applies are throttled rather than made per keystroke. The first selection
/// after a quiet spell is applied on the next dispatcher turn, so browsing
/// feels immediate; selections that arrive while that apply is queued, or
/// within <see cref="PreviewInterval"/> of the last one, only replace the
/// pending name, and the newest is applied when the interval ends. Holding an
/// arrow key down a long list therefore costs a handful of applies, not one
/// per row, and the theme the highlight stops on is always the one shown.
///
/// Every scheduled callback carries the browse it was made for. A callback
/// from a browse that has since ended (Escape, Enter, the palette closing, the
/// window going away) is dropped, so a reopen can never be painted over by a
/// straggler from the last one and nothing is applied after a revert.
///
/// Not thread-safe, and does not need to be: the palette drives it from the
/// UI thread, and the scheduler it is given calls back on that thread.
/// </summary>
public sealed class PaletteThemeBrowse
{
    /// <summary>The shortest gap between two applies while browsing.</summary>
    public static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(60);

    private readonly IThemePreviewTarget _target;
    private readonly InlineThemePreviewSession _session;
    private readonly Action<TimeSpan, Action> _schedule;

    private int _generation;
    private bool _active;
    private string? _pending;
    private string? _applied;
    private bool _flushQueued;
    private bool _coolingDown;
    private bool _recorded;
    private int _applyCount;

    /// <param name="target">What the previews are applied to.</param>
    /// <param name="session">
    /// The process-wide snapshot slot. Passed in rather than owned, because a
    /// second slot for the same palette is exactly the defect the shared one
    /// exists to prevent.
    /// </param>
    /// <param name="schedule">
    /// Runs the action on the UI thread after the delay (zero meaning the next
    /// dispatcher turn).
    /// </param>
    public PaletteThemeBrowse(
        IThemePreviewTarget target,
        InlineThemePreviewSession session,
        Action<TimeSpan, Action> schedule)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(schedule);
        _target = target;
        _session = session;
        _schedule = schedule;
    }

    /// <summary>Whether a browse is in progress.</summary>
    public bool IsActive => _active;

    /// <summary>The theme currently previewed by this browse, if any.</summary>
    public string? PreviewedTheme => _active ? _applied : null;

    /// <summary>
    /// The theme this browse is showing or about to show: the newest selection
    /// still waiting for its apply, else the one applied. Null before the first
    /// move, so the highlight a fresh list opens on is not reported as a
    /// preview, and null once the browse ends.
    /// </summary>
    public string? TargetTheme => _active ? _pending ?? _applied : null;

    /// <summary>
    /// Whether a selection is still waiting for its apply. The test seam
    /// waits on this so what it reads back is the settled preview.
    /// </summary>
    public bool HasPendingPreview => _active && (_pending is not null || _flushQueued);

    /// <summary>
    /// How many previews this browse has put on the live views. The test seam
    /// reads it to prove that typing a filter previews once, when it settles,
    /// and not once per keystroke.
    /// </summary>
    public int ApplyCount => _active ? _applyCount : 0;

    /// <summary>Start a browse. A browse already in progress is left alone.</summary>
    public void Begin()
    {
        if (_active) return;
        Reset();
        _active = true;
    }

    /// <summary>
    /// The highlight moved to <paramref name="themeName"/>. Null (a filter that
    /// matched nothing) leaves whatever is previewed where it is.
    /// </summary>
    public void Select(string? themeName)
    {
        if (!_active || themeName is null) return;
        _pending = themeName;
        if (_flushQueued || _coolingDown) return;

        _flushQueued = true;
        var generation = _generation;
        _schedule(TimeSpan.Zero, () =>
        {
            if (generation != _generation) return;
            _flushQueued = false;
            Flush();
        });
    }

    /// <summary>
    /// Escape, click-away, or any other dismissal: undo the previews and end
    /// the browse. A browse that never previewed anything touches nothing,
    /// not even the shared slot, which may be holding another browse's
    /// snapshot.
    /// </summary>
    public void Cancel()
    {
        if (!_active) return;
        var recorded = _recorded;
        Reset();
        if (!recorded) return;
        _target.Revert(_session.End());
    }

    /// <summary>
    /// Enter: keep <paramref name="themeName"/> and persist it, once. A
    /// selection still waiting for its apply is dropped; the commit's reload
    /// is what puts the chosen theme on screen.
    /// </summary>
    public void Confirm(string themeName)
    {
        ArgumentNullException.ThrowIfNull(themeName);
        if (!_active) return;
        Reset();
        // The colours the slot held are what the user just chose to replace,
        // so no later cancel, on any path, may put them back.
        _session.NoteConfirm();
        _target.Commit(themeName);
    }

    private void Flush()
    {
        if (!_active || _pending is not { } name) return;
        _pending = null;
        if (string.Equals(name, _applied, StringComparison.Ordinal)) return;

        // Recorded before the apply, so the slot holds what was on screen
        // before this browse, or any other, first changed it.
        _session.NotePreview(_target.CaptureColors);
        _recorded = true;
        _applyCount++;
        if (_target.ApplyPreview(name)) _applied = name;

        _coolingDown = true;
        var generation = _generation;
        _schedule(PreviewInterval, () =>
        {
            if (generation != _generation) return;
            _coolingDown = false;
            Flush();
        });
    }

    private void Reset()
    {
        _generation++;
        _active = false;
        _pending = null;
        _applied = null;
        _flushQueued = false;
        _coolingDown = false;
        _recorded = false;
        _applyCount = 0;
    }
}
