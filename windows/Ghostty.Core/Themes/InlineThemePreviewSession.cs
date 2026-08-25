using System;

namespace Ghostty.Core.Themes;

/// <summary>
/// The colour state a theme preview overwrites, and therefore the state a
/// cancelled preview has to put back.
/// </summary>
/// <param name="Foreground">Default text colour.</param>
/// <param name="Background">Default background colour.</param>
/// <param name="Cursor">Cursor colour, or null when it follows the foreground.</param>
/// <param name="CursorText">Cursor text colour, or null when it follows the background.</param>
/// <param name="Palette">The 16 ANSI colours.</param>
public readonly record struct ThemePreviewColors(
    uint Foreground,
    uint Background,
    uint? Cursor,
    uint? CursorText,
    uint[] Palette);

/// <summary>
/// One run of the inline theme picker, as far as the palette is concerned:
/// what the colours were before the first preview overwrote them, whether the
/// user ever accepted one, and therefore whether the close has to put the old
/// colours back.
///
/// Pure, and separate from the window for the same reason
/// <c>PipeServerRetryPolicy</c> and <c>ActiveWindowTarget</c> are: the test
/// project does not reference the WinUI shell, so logic left inline in
/// MainWindow is only ever exercised by a human arrowing through a theme list.
/// The defect this replaces was exactly that -- the window applied every theme
/// the picker named and had nothing that could tell a browse from a choice, so
/// arrowing past a theme and pressing Escape left it applied for good.
///
/// Two rules carry the whole thing, and both come from how the picker talks:
///
///   - The snapshot is taken lazily, once, before the FIRST preview. Taking it
///     on every callback would snapshot a preview and "revert" to it; taking
///     it when the picker opens would snapshot colours no preview may ever
///     touch. Hence a capture callback rather than a value: the caller cannot
///     accidentally pay for, or store, a snapshot that is not the first.
///   - The confirm is latched, not read off the last callback. Pressing Enter
///     makes the picker fire the confirm and then, on that same key, a preview
///     for the very theme it just confirmed -- so the final callback of a
///     successful run says "not confirmed", and a run whose outcome were read
///     from it would revert the theme the user chose.
///
/// Not thread-safe, and does not need to be: every caller reaches it from the
/// UI thread.
/// </summary>
public sealed class InlineThemePreviewSession
{
    private ThemePreviewColors? _saved;
    private bool _confirmed;

    /// <summary>
    /// Whether a preview has been noted and not yet ended. Diagnostic only --
    /// the decisions are <see cref="End"/>'s.
    /// </summary>
    public bool HasSnapshot => _saved is not null;

    /// <summary>
    /// A theme is about to be previewed. Captures the colours to restore, on
    /// the first call of a run only.
    /// </summary>
    /// <param name="capture">
    /// Reads the live colours. Invoked only when this is the first preview of
    /// the run, which is the point: on every later preview the live colours
    /// are a previous preview's, and capturing them would make the revert
    /// restore a theme the user never had.
    /// </param>
    public void NotePreview(Func<ThemePreviewColors> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (_saved is not null) return;

        var colors = capture();
        // Copied here rather than trusted from the caller: the shell's live
        // palette is a single array that theme application overwrites in
        // place, so a snapshot holding that same array is not a snapshot at
        // all -- the first preview would rewrite it and the revert would put
        // the previewed colours back.
        _saved = colors with { Palette = (uint[])colors.Palette.Clone() };
    }

    /// <summary>
    /// The user accepted a theme. Sticky for the rest of the run.
    /// </summary>
    public void NoteConfirm() => _confirmed = true;

    /// <summary>
    /// End the run and report what the caller must restore, or null when
    /// nothing should be put back -- the user confirmed, or no preview was
    /// ever applied.
    ///
    /// Resets, so the same instance serves the next time the picker opens.
    /// A picker that is opened, cancelled and opened again must snapshot the
    /// reverted colours the second time, not keep the first run's.
    /// </summary>
    public ThemePreviewColors? End()
    {
        var restore = _confirmed ? null : _saved;
        _saved = null;
        _confirmed = false;
        return restore;
    }
}
