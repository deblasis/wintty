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
/// The one theme browse in progress, as far as the palette is concerned:
/// what the colours were before a preview first overwrote them, and therefore
/// what an abandoned browse has to put back.
///
/// One of these serves the whole process, and that is the point rather than
/// an economy. A preview is not the picker's own drawing: it goes through the
/// app's single ConfigService, which overwrites one palette and repaints every
/// surface and all the chrome. Browses can genuinely overlap -- a theme
/// request goes to whichever window the user activated last and the pipe is
/// free again the moment the request is read, so a second one can arrive while
/// the first picker is still installed, and a picker and the pipe protocol can
/// be driving the palette at the same time. A snapshot per browse therefore
/// snapshots another browse's preview, and restoring it puts back a theme
/// nobody ever chose -- possibly over one somebody had just accepted.
///
/// So there is one slot, and three rules on it:
///
///   - The snapshot is taken lazily, before the first preview and no other.
///     Taking it on every callback would snapshot a preview and "revert" to
///     it; taking it when a picker opens would snapshot colours no preview may
///     ever touch. Hence a capture callback rather than a value: the caller
///     cannot accidentally pay for, or store, a snapshot that is not the
///     first. First writer wins, so a browse that starts while the slot is
///     full leaves it alone.
///   - A confirm empties the slot, for everyone. The colours it held are a
///     state the user has now browsed away from deliberately, so no later
///     cancel -- in any window, on either path -- may put them back. The next
///     preview after that snapshots the accepted colours, which is what makes
///     a browse-and-cancel that follows an accept return to the accepted
///     theme.
///   - Ending restores whatever is in the slot and empties it. Whoever closes
///     first spends the snapshot; the runs that follow find it empty and have
///     nothing to undo, which is correct, because by then the colours are back
///     to what nobody browsed away from.
///
/// Ending is the caller's close rather than a message from the picker,
/// because there is no reliable cancel message to wait for. Escape and ^C set
/// should_quit and fall through to a notify that fires only when the selection
/// has moved since the last one, so once the user has arrowed at all the
/// cancel is silent. The exception is a cancel on the very first key, before
/// anything has notified: that one does fire a preview, for the theme the list
/// opened on. Both endings are absorbed here -- the preview is either dropped
/// before it reaches this or recorded and then undone by the very close that
/// follows it -- which is why the close decides and the last callback seen
/// never does.
///
/// There is deliberately no per-run state here. Which browse took the
/// snapshot is not a question this can answer once two of them share a
/// palette, and every attempt to answer it is how the slot ends up holding a
/// theme the user never had. Callers that need to know which of their own
/// callbacks is current keep that with the run, not here.
///
/// Not thread-safe, and does not need to be: every caller reaches it from the
/// UI thread, the pipe path by dispatching there first.
/// </summary>
public sealed class InlineThemePreviewSession
{
    private ThemePreviewColors? _saved;

    /// <summary>
    /// A theme is about to be previewed. Captures the colours to restore, if
    /// the slot is empty.
    /// </summary>
    /// <param name="capture">
    /// Reads the live colours. Invoked only when the slot is empty, which is
    /// the point: otherwise the live colours are already some preview's, and
    /// capturing them would make the revert restore a theme the user never
    /// had.
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
    /// The user accepted a theme. The colours the slot was holding are gone:
    /// they are what the accepted theme replaced, and nothing may restore them
    /// over the user's choice.
    /// </summary>
    // Not a flag that a later End reads. A latch would have to belong to one
    // browse, and there can be two -- accept in one window and the other one's
    // cancel would either be silenced by a latch it has nothing to do with, or
    // read a slot that still held colours from before the accept and paint
    // them over it.
    public void NoteConfirm() => _saved = null;

    /// <summary>
    /// End a browse and report what the caller must restore, or null when
    /// nothing should be put back -- the slot is empty because a confirm
    /// emptied it, because no preview was ever applied, or because an earlier
    /// close already spent it.
    /// </summary>
    public ThemePreviewColors? End()
    {
        var restore = _saved;
        _saved = null;
        return restore;
    }
}
