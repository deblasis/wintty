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
        latch.MarkApplied(latch.Wanted, latch.Wanted);
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
        latch.MarkApplied(latch.Wanted, latch.Wanted);

        Assert.True(latch.Request(null));   // declined
        latch.Request(Black);               // declined, or skipped

        Assert.Equal(Black, latch.Wanted);
        latch.MarkApplied(latch.Wanted, latch.Wanted);
        Assert.Equal(Black, latch.Applied);
    }

    /// <summary>
    /// A reload that applied without layering the palette (its override
    /// file could not be written) records none as applied, so the splash is
    /// not told a palette the screen does not show. The palette stays
    /// wanted, so the next reload for any other reason builds with it and
    /// writes the file again. A repeat of the SAME request does not reload
    /// on its own: that repeat is the monitor answering the reload's own
    /// ConfigChanged, and reloading on it never stops (see below). A
    /// different palette does reload.
    /// </summary>
    [Fact]
    public void A_reload_that_could_not_layer_the_palette_retries_only_on_a_change()
    {
        var latch = new HighContrastOverrideLatch();

        Assert.True(latch.Request(Black));
        latch.MarkApplied(null, attempted: Black);

        Assert.Null(latch.Applied);
        Assert.Equal(Black, latch.Wanted);
        Assert.False(latch.Request(Black));
        Assert.True(latch.Request(White));
    }

    /// <summary>
    /// The loop, with the monitor in it. High Contrast is on and the
    /// override file cannot be written (a full disk, a read-only
    /// high-contrast.conf, an unwritable state directory). Every applied
    /// reload skips the layer, raises ConfigChanged, and HighContrastMonitor
    /// answers ConfigChanged by asking for the same palette again. Skipping
    /// only on Applied let every one of those through, and the app reloaded
    /// forever: a config build and a push to every surface per turn. The
    /// request that started it reloads once, and the monitor's repeat is
    /// skipped.
    /// </summary>
    [Fact]
    public void A_palette_that_cannot_be_written_does_not_reload_forever()
    {
        var latch = new HighContrastOverrideLatch();
        var dispatcher = new System.Collections.Generic.Queue<System.Action>();
        var reloads = 0;

        // ConfigService.SetHighContrastOverride, and the applied path of
        // Reload with a write that always fails: the build takes one read of
        // Wanted, lays nothing, and ConfigChanged posts the monitor's Apply.
        void SetOverride(HighContrastColors? colors)
        {
            if (!latch.Request(colors)) return;
            reloads++;
            var wanted = latch.Wanted;
            HighContrastColors? layered = null;
            latch.MarkApplied(layered, attempted: wanted);
            dispatcher.Enqueue(() => SetOverride(Black));
        }

        SetOverride(Black);
        for (var turn = 0; turn < 100 && dispatcher.Count > 0; turn++)
            dispatcher.Dequeue()();

        Assert.Equal(1, reloads);
        Assert.Empty(dispatcher);
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
        latch.MarkApplied(latch.Wanted, latch.Wanted);
        Assert.False(latch.Request(Black));

        Assert.True(latch.Request(null));
        latch.MarkApplied(latch.Wanted, latch.Wanted);
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
        latch.MarkApplied(built, built);

        Assert.Equal(Black, latch.Applied);
        Assert.Equal(White, latch.Wanted);
        Assert.True(latch.Request(White));
    }
}
