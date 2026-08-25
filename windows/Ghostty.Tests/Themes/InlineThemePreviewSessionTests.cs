using System;
using Ghostty.Core.Themes;
using Xunit;

namespace Ghostty.Tests.Themes;

/// <summary>
/// What a cancelled theme preview has to put back.
///
/// The inline picker applies a theme on every arrow key and reports the
/// difference between browsing and choosing in one bool on its callback. The
/// window ignored that bool, so arrowing past a theme and pressing Escape left
/// it applied exactly as if the user had chosen it. These pin the two facts
/// that make a revert correct, both of which are easy to get backwards while
/// the feature still looks like it works: the snapshot is of the colours
/// BEFORE the first preview, and a confirm cannot be un-confirmed by the
/// preview callback that follows it.
/// </summary>
public class InlineThemePreviewSessionTests
{
    private static ThemePreviewColors Colors(uint seed) => new(
        Foreground: seed,
        Background: seed + 1,
        Cursor: seed + 2,
        CursorText: seed + 3,
        Palette: Palette(seed));

    private static uint[] Palette(uint seed)
    {
        var palette = new uint[16];
        for (var i = 0; i < palette.Length; i++) palette[i] = seed + (uint)i;
        return palette;
    }

    /// <summary>
    /// A picker opened and dismissed without ever moving the selection never
    /// overwrote anything, so there is nothing to restore. Reverting anyway
    /// would push a full theme apply -- and the repaint of every surface that
    /// comes with it -- through on a picker that changed no colours at all.
    /// </summary>
    [Fact]
    public void ARunWithNoPreviewRestoresNothing()
    {
        var session = new InlineThemePreviewSession();

        Assert.False(session.HasSnapshot);
        Assert.Null(session.End());
    }

    /// <summary>
    /// The snapshot is of the colours before the FIRST preview. Capturing on
    /// every callback is the mutation that matters: each capture would record
    /// the previous preview's colours, so cancelling after browsing three
    /// themes would "revert" to the second one and the user would still be
    /// left with a theme they rejected.
    /// </summary>
    [Fact]
    public void TheFirstPreviewIsWhatGetsRestored()
    {
        var session = new InlineThemePreviewSession();

        session.NotePreview(() => Colors(100));
        session.NotePreview(() => Colors(200));
        session.NotePreview(() => Colors(300));

        var restore = session.End();
        Assert.NotNull(restore);
        Assert.Equal(100u, restore!.Value.Foreground);
        Assert.Equal(101u, restore.Value.Background);
        Assert.Equal(102u, restore.Value.Cursor);
        Assert.Equal(103u, restore.Value.CursorText);
        Assert.Equal(Palette(100), restore.Value.Palette);
    }

    /// <summary>
    /// And the capture itself runs once, not once per preview. Reading the
    /// live colours is a real cost on a keystroke path, but the reason this is
    /// pinned separately is correctness: a session that captured every time
    /// and merely kept the first result would still be handing the caller's
    /// capture a chance to run against colours it must never see.
    /// </summary>
    [Fact]
    public void TheCaptureRunsOnlyOnTheFirstPreview()
    {
        var session = new InlineThemePreviewSession();
        var captures = 0;

        for (var i = 0; i < 5; i++)
        {
            session.NotePreview(() =>
            {
                captures++;
                return Colors(10);
            });
        }

        Assert.Equal(1, captures);
    }

    /// <summary>
    /// A theme the user accepted stays. Nothing to restore.
    /// </summary>
    [Fact]
    public void AConfirmedRunRestoresNothing()
    {
        var session = new InlineThemePreviewSession();

        session.NotePreview(() => Colors(100));
        session.NoteConfirm();

        Assert.Null(session.End());
    }

    /// <summary>
    /// The sharp one. Pressing Enter makes the picker fire the confirm and
    /// then, on that same key, a preview for the theme it just confirmed -- so
    /// the LAST callback of an accepted run says "not confirmed". A session
    /// that tracked the most recent callback instead of latching the confirm
    /// would revert the theme the user chose, which is the original defect
    /// with the sign flipped and no more visible.
    /// </summary>
    [Fact]
    public void ATrailingPreviewDoesNotUndoTheConfirm()
    {
        var session = new InlineThemePreviewSession();

        session.NotePreview(() => Colors(100));
        session.NoteConfirm();
        session.NotePreview(() => Colors(200));

        Assert.Null(session.End());
    }

    /// <summary>
    /// Enter as the very first key confirms without any preview having been
    /// applied first. Still nothing to restore.
    /// </summary>
    [Fact]
    public void AConfirmWithNoPreviewRestoresNothing()
    {
        var session = new InlineThemePreviewSession();

        session.NoteConfirm();
        session.NotePreview(() => Colors(100));

        Assert.Null(session.End());
    }

    /// <summary>
    /// The shell's live palette is one array that applying a theme overwrites
    /// in place, so a snapshot that holds that array is not a snapshot: the
    /// first preview rewrites it and the revert puts the previewed colours
    /// back. Copying it is the session's job precisely because a caller
    /// forgetting to is invisible -- every other assertion here still passes.
    /// </summary>
    [Fact]
    public void TheSnapshotDoesNotAliasTheCallersPalette()
    {
        var session = new InlineThemePreviewSession();
        var live = Palette(100);

        session.NotePreview(() => new ThemePreviewColors(1, 2, 3, 4, live));

        // What applying a theme does to that same array.
        Array.Fill(live, 0xDEADBEEF);

        var restore = session.End();
        Assert.NotNull(restore);
        Assert.Equal(Palette(100), restore!.Value.Palette);
    }

    /// <summary>
    /// The picker can be opened, cancelled, and opened again. The second run
    /// has to snapshot the colours it finds -- the reverted ones -- rather
    /// than keep the first run's, and it has to start un-confirmed however the
    /// first one ended.
    /// </summary>
    [Fact]
    public void EachRunSnapshotsAgain()
    {
        var session = new InlineThemePreviewSession();

        session.NotePreview(() => Colors(100));
        var first = session.End();
        Assert.NotNull(first);
        Assert.Equal(100u, first!.Value.Foreground);

        session.NotePreview(() => Colors(200));
        var second = session.End();
        Assert.NotNull(second);
        Assert.Equal(200u, second!.Value.Foreground);
    }

    /// <summary>
    /// A confirm does not leak into the next run either: the picker reopened
    /// after an accepted one must still be revertible.
    /// </summary>
    [Fact]
    public void AConfirmDoesNotSurviveIntoTheNextRun()
    {
        var session = new InlineThemePreviewSession();

        session.NotePreview(() => Colors(100));
        session.NoteConfirm();
        Assert.Null(session.End());

        session.NotePreview(() => Colors(200));
        var reopened = session.End();
        Assert.NotNull(reopened);
        Assert.Equal(200u, reopened!.Value.Foreground);
    }

    /// <summary>
    /// Ending twice restores once. The close is reached from several places
    /// and is written to be safe to call with no picker running.
    /// </summary>
    [Fact]
    public void EndingTwiceRestoresOnce()
    {
        var session = new InlineThemePreviewSession();

        session.NotePreview(() => Colors(100));

        Assert.NotNull(session.End());
        Assert.Null(session.End());
    }

    /// <summary>
    /// HasSnapshot reports the run, not the last call: it is what a caller
    /// would read to decide whether a revert is pending.
    /// </summary>
    [Fact]
    public void HasSnapshotTracksTheRun()
    {
        var session = new InlineThemePreviewSession();
        Assert.False(session.HasSnapshot);

        session.NotePreview(() => Colors(100));
        Assert.True(session.HasSnapshot);

        session.End();
        Assert.False(session.HasSnapshot);
    }

    /// <summary>
    /// A null capture is a programming error, not a run with nothing to
    /// restore: swallowing it would leave the revert silently disarmed.
    /// </summary>
    [Fact]
    public void ANullCaptureThrows()
    {
        var session = new InlineThemePreviewSession();

        Assert.Throws<ArgumentNullException>(() => session.NotePreview(null!));
    }
}
