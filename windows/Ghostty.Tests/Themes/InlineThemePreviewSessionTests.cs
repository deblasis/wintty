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
/// it applied exactly as if the user had chosen it.
///
/// These pin the facts that make a revert correct, all of which are easy to
/// get backwards while the feature still looks like it works. Two are about
/// one browse: the snapshot is of the colours BEFORE the first preview, and it
/// is a copy. The other two are about the fact that browses share a palette
/// and therefore share this: a second browse must not overwrite a snapshot the
/// first one is still going to need, and an accept anywhere has to empty the
/// slot everywhere, because the colours it held are exactly what the user just
/// chose to replace.
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
    /// A picker opened and dismissed without ever previewing anything never
    /// overwrote a colour, so there is nothing to restore. Reverting anyway
    /// would push a full theme apply -- and the repaint of every surface that
    /// comes with it -- through on a picker that changed nothing at all.
    /// </summary>
    [Fact]
    public void ARunWithNoPreviewRestoresNothing()
    {
        var session = new InlineThemePreviewSession();

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
    /// The one that carries the ownership. Two browses can be live at once --
    /// a theme request goes to whichever window the user activated last, and
    /// the pipe is free again the moment it is read, so a second invocation
    /// connects while the first picker is still installed. An accept in one of
    /// them has to empty the slot for both: the colours it was holding are
    /// precisely the ones the accepted theme replaced, and nothing may put
    /// them back over the user's choice.
    ///
    /// The mutation this exists for is a confirm that latches a flag instead,
    /// which is what a per-browse session had to do. The flag then belongs to
    /// no browse in particular: the other window keeps browsing, gets no
    /// snapshot because the slot was never emptied, and its Escape is silenced
    /// by a latch it has nothing to do with -- leaving whatever it had browsed
    /// to on screen, over the theme the user accepted. So the assertion is not
    /// that the cancel restores nothing; it is that it restores the ACCEPTED
    /// colours, which only an emptied slot can produce.
    /// </summary>
    [Fact]
    public void AConfirmEmptiesTheSlotForEveryBrowse()
    {
        var session = new InlineThemePreviewSession();

        // A picker in one window browses away from the live colours.
        session.NotePreview(() => Colors(100));

        // A second browse accepts a theme. The 100s are what it replaced.
        session.NoteConfirm();

        // The first window browses on, and finds the slot empty: what it arms
        // it with is the accepted theme, live now.
        session.NotePreview(() => Colors(300));

        // So cancelling it goes back to the accepted theme, not past it.
        var restore = session.End();
        Assert.NotNull(restore);
        Assert.Equal(300u, restore!.Value.Foreground);
    }

    /// <summary>
    /// And the slot is first-writer-wins across browses, not just within one.
    /// The second browse arrives to find colours that are already a preview's
    /// -- snapshotting them would arm the revert with a theme nobody chose,
    /// and whichever close came first would apply it.
    ///
    /// Both halves are asserted: the later capture must not even run (it reads
    /// live state that is wrong by then), and the snapshot handed back must be
    /// the first browse's.
    /// </summary>
    [Fact]
    public void AnOverlappingBrowseDoesNotOverwriteTheSnapshot()
    {
        var session = new InlineThemePreviewSession();
        var second = 0;

        session.NotePreview(() => Colors(100));
        session.NotePreview(() =>
        {
            second++;
            return Colors(200);
        });

        Assert.Equal(0, second);

        // Whichever browse closes first spends it.
        var restore = session.End();
        Assert.NotNull(restore);
        Assert.Equal(100u, restore!.Value.Foreground);

        // And the other one finds it empty rather than reverting a second
        // time over colours that are already back.
        Assert.Null(session.End());
    }

    /// <summary>
    /// Enter as the very first key confirms without the selection having
    /// moved, so the picker's notify -- which fires only on a change -- has
    /// never run. It runs now, after the confirm, producing one preview for
    /// the theme just accepted.
    ///
    /// That preview re-arms the slot with the accepted colours, which is
    /// correct rather than merely harmless: it is the state a browse starting
    /// after this accept has to be able to return to. The cost is that the
    /// close then re-applies colours that are already on screen, once, and
    /// only on this one key sequence -- after any arrow key the notify has
    /// already fired for that theme and no echo follows the confirm at all.
    /// Suppressing it would need a flag belonging to one browse, which is the
    /// thing this type refuses to have.
    /// </summary>
    [Fact]
    public void TheEchoPreviewAfterAConfirmReArmsTheSlot()
    {
        var session = new InlineThemePreviewSession();

        session.NoteConfirm();
        session.NotePreview(() => Colors(100));

        var restore = session.End();
        Assert.NotNull(restore);
        Assert.Equal(100u, restore!.Value.Foreground);
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
    /// than keep the first run's.
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
