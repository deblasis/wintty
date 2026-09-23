using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

/// <summary>
/// The High Contrast request and what the running config carries, kept
/// apart so a declined reload neither latches High Contrast off nor loses
/// the request (wintty#1155).
/// </summary>
public class HighContrastOverrideLatchTests
{
    private static readonly HighContrastColors Black =
        new(Background: 0x000000, Foreground: 0xFFFFFF,
            SelectionBackground: 0x00FFFF, SelectionForeground: 0x000000);

    private static readonly HighContrastColors White =
        new(Background: 0xFFFFFF, Foreground: 0x000000,
            SelectionBackground: 0x800000, SelectionForeground: 0xFFFFFF);

    /// <summary>
    /// A request whose reload declined is still wanted, so the next reload
    /// that applies carries it, whoever causes that reload. Putting the old
    /// value back on a decline is what made a toggle made while a deleted
    /// config file was being confirmed vanish: the reload the vanish
    /// question scheduled for itself then built without it.
    /// </summary>
    [Fact]
    public void A_declined_request_rides_the_next_reload_that_applies()
    {
        var latch = new HighContrastOverrideLatch();

        Assert.True(latch.Request(Black));
        // The reload declined: nothing is marked applied.

        // A later reload, not caused by the monitor, builds with it.
        Assert.Equal(Black, latch.Wanted);
        latch.MarkApplied(latch.Wanted);
        Assert.Equal(Black, latch.Applied);
    }

    /// <summary>
    /// And a repeat of a request that never applied is tried again rather
    /// than answered "already on this palette". Leaving one field moved on
    /// a decline answered exactly that, so one declined reload turned High
    /// Contrast off for the life of the process.
    /// </summary>
    [Fact]
    public void A_repeat_of_a_request_that_never_applied_reloads_again()
    {
        var latch = new HighContrastOverrideLatch();

        Assert.True(latch.Request(Black));
        Assert.True(latch.Request(Black));
    }

    /// <summary>
    /// Off then on again while reloads decline ends ON. The request to go
    /// back to the palette the running config carries is still recorded as
    /// wanted, because the declined "off" moved what the next reload would
    /// build. Skipping it on Applied alone left Wanted at off, and the next
    /// reload that applied, the vanish question's own look, turned High
    /// Contrast off while the OS had it on.
    /// </summary>
    [Fact]
    public void Off_then_on_while_reloads_decline_ends_on()
    {
        var latch = new HighContrastOverrideLatch();
        Assert.True(latch.Request(Black));
        latch.MarkApplied(latch.Wanted);

        Assert.True(latch.Request(null));   // declined
        latch.Request(Black);               // declined, or skipped

        Assert.Equal(Black, latch.Wanted);
        latch.MarkApplied(latch.Wanted);
        Assert.Equal(Black, latch.Applied);
    }

    /// <summary>
    /// A reload that applied without layering the palette (its override
    /// file could not be written) records none, so the palette is still
    /// wanted and a repeat of the request reloads rather than being answered
    /// "already on this palette" over a screen that is not showing it.
    /// </summary>
    [Fact]
    public void A_reload_that_could_not_layer_the_palette_leaves_it_to_ask_again()
    {
        var latch = new HighContrastOverrideLatch();

        Assert.True(latch.Request(Black));
        latch.MarkApplied(null);

        Assert.Null(latch.Applied);
        Assert.True(latch.Request(Black));
    }

    /// <summary>
    /// A request the running config already carries costs nothing: the
    /// monitor calls on every palette event and on every ConfigChanged, and
    /// each applied reload raises one.
    /// </summary>
    [Fact]
    public void A_request_the_running_config_already_carries_is_skipped()
    {
        var latch = new HighContrastOverrideLatch();

        Assert.False(latch.Request(null));

        Assert.True(latch.Request(Black));
        latch.MarkApplied(latch.Wanted);
        Assert.False(latch.Request(Black));

        Assert.True(latch.Request(null));
        latch.MarkApplied(latch.Wanted);
        Assert.False(latch.Request(null));
        Assert.Null(latch.Applied);
    }

    /// <summary>
    /// Applied records what a reload was BUILT with, so a request made while
    /// that reload was in flight stays wanted and is not reported as on
    /// screen.
    /// </summary>
    [Fact]
    public void Applied_records_what_was_built_not_what_was_asked_since()
    {
        var latch = new HighContrastOverrideLatch();

        Assert.True(latch.Request(Black));
        var built = latch.Wanted;
        Assert.True(latch.Request(White));
        latch.MarkApplied(built);

        Assert.Equal(Black, latch.Applied);
        Assert.Equal(White, latch.Wanted);
        Assert.True(latch.Request(White));
    }
}
