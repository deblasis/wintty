using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Themes;
using Xunit;

namespace Ghostty.Tests.Themes;

/// <summary>
/// The command palette's theme mode: the highlight re-themes the live views,
/// Escape restores them exactly, Enter persists once, and nothing a browse
/// scheduled can land after it ended.
///
/// The target here is a fake with the same in-place palette semantics the
/// shell's config service has (a restore copies into the live array rather
/// than replacing it), so "exactly" is checked the way the chrome reads it.
/// </summary>
public class PaletteThemeBrowseTests
{
    private sealed class Clock
    {
        public List<(TimeSpan Delay, Action Run)> Queued { get; } = new();

        public void Schedule(TimeSpan delay, Action run) => Queued.Add((delay, run));

        /// <summary>Run what is due now (zero delay), in order.</summary>
        public void Turn()
        {
            while (Queued.FirstOrDefault(q => q.Delay == TimeSpan.Zero) is { Run: not null } next)
            {
                Queued.Remove(next);
                next.Run();
            }
        }

        /// <summary>Let every interval elapse, running whatever it schedules.</summary>
        public void Elapse()
        {
            for (var guard = 0; guard < 100 && Queued.Count > 0; guard++)
            {
                var next = Queued[0];
                Queued.RemoveAt(0);
                next.Run();
            }
        }
    }

    private sealed class Target : IThemePreviewTarget
    {
        public uint Foreground = 0xC0C0C0;
        public uint Background = 0x101010;
        public uint Cursor = 0xAAAAAA;
        public uint CursorText = 0x101010;
        public readonly uint[] Palette = Enumerable.Range(0, 16).Select(i => (uint)(0x010101 * i)).ToArray();

        public string? Native = "committed";
        public List<string> Applied { get; } = new();
        public List<ThemePreviewColors?> Reverts { get; } = new();
        public List<string> Commits { get; } = new();
        public HashSet<string> Missing { get; } = new();

        public ThemePreviewColors CaptureColors() =>
            new(Foreground, Background, Cursor, CursorText, Palette);

        public bool ApplyPreview(string themeName)
        {
            if (Missing.Contains(themeName)) return false;
            Applied.Add(themeName);
            Native = themeName;
            var seed = (uint)themeName.GetHashCode() & 0x00FFFFFF;
            Foreground = seed ^ 0xFFFFFF;
            Background = seed;
            Cursor = seed ^ 0x0F0F0F;
            CursorText = seed ^ 0xF0F0F0;
            for (var i = 0; i < Palette.Length; i++) Palette[i] = seed + (uint)i;
            return true;
        }

        public void Revert(ThemePreviewColors? colors)
        {
            Reverts.Add(colors);
            Native = "committed";
            if (colors is not { } c) return;
            Foreground = c.Foreground;
            Background = c.Background;
            Cursor = c.Cursor ?? c.Foreground;
            CursorText = c.CursorText ?? c.Background;
            Array.Copy(c.Palette, Palette, 16);
        }

        public void Commit(string themeName)
        {
            Commits.Add(themeName);
            Native = themeName;
        }

        public byte[] Bytes()
        {
            var words = new List<uint> { Foreground, Background, Cursor, CursorText };
            words.AddRange(Palette);
            return words.SelectMany(w => BitConverter.GetBytes(w)).ToArray();
        }
    }

    /// <summary>
    /// The palette's "Previewing" badge and announcement read this: nothing
    /// for the highlight a fresh list opens on, the newest selection as soon
    /// as it is made, what is actually on screen when an apply fails, and
    /// nothing once the browse is over.
    /// </summary>
    [Fact]
    public void TheTargetIsWhatTheBrowseShowsOrIsAboutToShow()
    {
        var (browse, target, clock, _) = Make();
        Assert.Null(browse.TargetTheme);

        browse.Begin();
        Assert.Null(browse.TargetTheme);

        browse.Select("Alpha");
        Assert.Equal("Alpha", browse.TargetTheme);
        clock.Turn();
        Assert.Equal("Alpha", browse.TargetTheme);

        target.Missing.Add("Broken");
        browse.Select("Broken");
        Assert.Equal("Broken", browse.TargetTheme);
        clock.Elapse();
        Assert.Equal("Alpha", browse.TargetTheme);

        browse.Cancel();
        Assert.Null(browse.TargetTheme);
    }

    private static (PaletteThemeBrowse Browse, Target Target, Clock Clock, InlineThemePreviewSession Session) Make()
    {
        var target = new Target();
        var clock = new Clock();
        var session = new InlineThemePreviewSession();
        return (new PaletteThemeBrowse(target, session, clock.Schedule), target, clock, session);
    }

    /// <summary>
    /// Escape after browsing several themes restores every chrome colour byte
    /// for byte and puts the committed config back on the terminal, once.
    /// </summary>
    [Fact]
    public void EscapeRestoresTheOriginalExactly()
    {
        var (browse, target, clock, _) = Make();
        var original = target.Bytes();

        browse.Begin();
        foreach (var theme in new[] { "alpha", "beta", "gamma" })
        {
            browse.Select(theme);
            clock.Turn();
            clock.Elapse();
        }
        Assert.Equal("gamma", target.Native);
        Assert.NotEqual(original, target.Bytes());

        browse.Cancel();

        Assert.Equal(original, target.Bytes());
        Assert.Equal("committed", target.Native);
        Assert.Single(target.Reverts);
        Assert.Empty(target.Commits);
        Assert.False(browse.IsActive);
    }

    /// <summary>
    /// Enter persists exactly one change and reverts nothing, and a preview
    /// that was still queued behind it never lands.
    /// </summary>
    [Fact]
    public void ConfirmCommitsExactlyOnceAndNeverReverts()
    {
        var (browse, target, clock, _) = Make();

        browse.Begin();
        browse.Select("alpha");
        clock.Turn();
        browse.Select("beta");        // queued behind the cooldown
        browse.Confirm("beta");
        clock.Elapse();               // the straggler fires into an ended browse

        Assert.Equal(new[] { "beta" }, target.Commits);
        Assert.Empty(target.Reverts);
        Assert.Equal(new[] { "alpha" }, target.Applied);
        Assert.Equal("beta", target.Native);

        // A second Enter or a stray close after the confirm is inert.
        browse.Confirm("beta");
        browse.Cancel();
        Assert.Single(target.Commits);
        Assert.Empty(target.Reverts);
    }

    /// <summary>
    /// A burst of selections costs one apply per interval, and the last
    /// highlighted theme is the one left on screen.
    /// </summary>
    [Fact]
    public void RapidNavigationIsThrottledAndEndsOnTheLastSelection()
    {
        var (browse, target, clock, _) = Make();
        browse.Begin();

        foreach (var theme in new[] { "t1", "t2", "t3", "t4" }) browse.Select(theme);
        clock.Turn();
        Assert.Equal(new[] { "t4" }, target.Applied);

        foreach (var theme in new[] { "t5", "t6", "t7" }) browse.Select(theme);
        Assert.Equal(new[] { "t4" }, target.Applied);
        Assert.True(browse.HasPendingPreview);

        clock.Elapse();
        Assert.Equal(new[] { "t4", "t7" }, target.Applied);
        Assert.Equal("t7", browse.PreviewedTheme);
        Assert.False(browse.HasPendingPreview);
    }

    /// <summary>
    /// Reopening after a cancel starts clean: callbacks from the old browse
    /// are dropped, and the new snapshot is of the restored colours.
    /// </summary>
    [Fact]
    public void ReopenNeverSeesTheLastBrowsesStragglers()
    {
        var (browse, target, clock, _) = Make();
        var original = target.Bytes();

        browse.Begin();
        browse.Select("alpha");       // queued, not yet applied
        browse.Cancel();
        Assert.Empty(target.Reverts); // nothing was applied, so nothing to undo

        browse.Begin();
        clock.Elapse();               // the old browse's callback runs now
        Assert.Empty(target.Applied);

        browse.Select("beta");
        clock.Turn();
        browse.Cancel();
        Assert.Equal(new[] { "beta" }, target.Applied);
        Assert.Equal(original, target.Bytes());
    }

    /// <summary>
    /// The case the issue calls out: a user who never set a theme. The
    /// committed config is what comes back, not "some default theme".
    /// </summary>
    [Fact]
    public void AnUnthemedConfigRevertsToTheCommittedConfigNotADefault()
    {
        var (browse, target, clock, _) = Make();
        browse.Begin();
        browse.Select("alpha");
        clock.Turn();
        browse.Cancel();
        Assert.Equal("committed", target.Native);
    }

    /// <summary>
    /// A browse that never previewed must not spend the shared snapshot: the
    /// inline picker may be mid-browse in another window and still needs it.
    /// </summary>
    [Fact]
    public void ACancelWithoutAPreviewLeavesTheSharedSlotAlone()
    {
        var (browse, _, _, session) = Make();
        var theirs = new ThemePreviewColors(1, 2, 3, 4, new uint[16]);
        session.NotePreview(() => theirs);

        browse.Begin();
        browse.Cancel();

        Assert.Equal(theirs.Background, session.End()!.Value.Background);
    }

    /// <summary>
    /// Overlapping another browse of the same palette: the palette's Escape
    /// restores the colours from before either browse, never the other
    /// browse's preview. This is the torn-snapshot shape, and it cannot arise
    /// here because both browses share one slot and it is filled once.
    /// </summary>
    [Fact]
    public void OverlappingBrowsesRestoreTheTrueOriginal()
    {
        var (browse, target, clock, session) = Make();
        var original = target.Bytes();

        // Another browse (the inline picker) snapshots and previews first.
        session.NotePreview(target.CaptureColors);
        target.ApplyPreview("pickers-preview");
        target.Applied.Clear();

        browse.Begin();
        browse.Select("alpha");
        clock.Turn();
        browse.Cancel();

        Assert.Equal(original, target.Bytes());
        Assert.Null(session.End());   // spent once, by whoever closed first
    }

    /// <summary>
    /// After a confirm the snapshot slot is empty, so the next browse
    /// snapshots the committed colours and its Escape returns to them.
    /// </summary>
    [Fact]
    public void ABrowseAfterAConfirmRevertsToTheConfirmedTheme()
    {
        var (browse, target, clock, _) = Make();
        browse.Begin();
        browse.Select("alpha");
        clock.Turn();
        browse.Confirm("alpha");
        var afterConfirm = target.Bytes();
        clock.Elapse();

        browse.Begin();
        browse.Select("beta");
        clock.Turn();
        browse.Cancel();

        Assert.Equal(afterConfirm, target.Bytes());
    }

    /// <summary>
    /// A theme that cannot be applied changes nothing and is not reported as
    /// previewed; the next selection still applies.
    /// </summary>
    [Fact]
    public void AThemeThatFailsToApplyIsNotReportedAsPreviewed()
    {
        var (browse, target, clock, _) = Make();
        target.Missing.Add("ghost");
        browse.Begin();

        browse.Select("ghost");
        clock.Turn();
        Assert.Null(browse.PreviewedTheme);

        clock.Elapse();
        browse.Select("alpha");
        clock.Turn();
        Assert.Equal("alpha", browse.PreviewedTheme);
    }

    /// <summary>Selections outside a browse, or of nothing, are ignored.</summary>
    [Fact]
    public void SelectionsOutsideABrowseOrOfNothingAreIgnored()
    {
        var (browse, target, clock, _) = Make();
        browse.Select("alpha");
        browse.Begin();
        browse.Select(null);
        clock.Elapse();
        Assert.Empty(target.Applied);
        Assert.Empty(clock.Queued);
    }
}
