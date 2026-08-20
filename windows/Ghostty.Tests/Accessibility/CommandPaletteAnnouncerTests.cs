using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class CommandPaletteAnnouncerTests
{
    [Theory]
    [InlineData("Search", "Search mode")]
    [InlineData("Command", "Command mode")]
    public void ModeAccessibleName_CarriesTheMode(string label, string expected)
    {
        Assert.Equal(expected, CommandPaletteAnnouncer.ModeAccessibleName(label));
    }

    [Fact]
    public void ModeAccessibleName_IsNeverTheBareModeWord()
    {
        // A name equal to a command title makes a find-by-name land on the
        // header label instead of the row, which is what put the fuzz
        // harness's click on the wrong element.
        Assert.NotEqual("Search", CommandPaletteAnnouncer.ModeAccessibleName("Search"));
        Assert.NotEqual("Command", CommandPaletteAnnouncer.ModeAccessibleName("Command"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ModeAccessibleName_FallsBackWhenThereIsNoMode(string? label)
    {
        Assert.Equal("Palette mode", CommandPaletteAnnouncer.ModeAccessibleName(label));
    }

    // ── Repeat suppression ───────────────────────────────────────────────

    [Fact]
    public void StatusChanged_SpeaksTheFirstCount()
    {
        var a = Live();
        Assert.Equal("12 commands", a.StatusChanged("12 commands"));
    }

    [Fact]
    public void StatusChanged_DropsAnUnchangedCount()
    {
        var a = Live();
        a.StatusChanged("12 commands");
        Assert.Null(a.StatusChanged("12 commands"));
    }

    [Fact]
    public void StatusChanged_SpeaksAgainWhenTheCountMoves()
    {
        var a = Live();
        a.StatusChanged("12 commands");
        Assert.Equal("3 commands", a.StatusChanged("3 commands"));
        Assert.Equal("1 command", a.StatusChanged("1 command"));
    }

    [Fact]
    public void StatusChanged_SpeaksTheModeSwitchToActions()
    {
        // Typing ">" flips the palette to command mode. The count wording
        // changes with it, so the switch is audible even though nothing
        // announces the mode label directly.
        var a = Live();
        a.StatusChanged("3 commands");
        Assert.Equal("3 actions", a.StatusChanged("3 actions"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void StatusChanged_SaysNothingForAnEmptyStatus(string? status)
    {
        Assert.Null(Live().StatusChanged(status));
    }

    [Fact]
    public void StatusChanged_EmptyStatusDoesNotPoisonTheNextRealOne()
    {
        var a = Live();
        a.StatusChanged("5 commands");
        a.StatusChanged("");
        // The blank was never spoken, so the count that follows it is only
        // a repeat if it repeats what was actually spoken.
        Assert.Null(a.StatusChanged("5 commands"));
        Assert.Equal("6 commands", a.StatusChanged("6 commands"));
    }

    // ── Hold until focus ─────────────────────────────────────────────────

    [Fact]
    public void Opening_HoldsEveryCountRaisedBeforeFocusArrives()
    {
        // The view model raises IsOpen, then the filter results, then the
        // count, and only after all of that does the host move focus into
        // the palette. Anything spoken in that window is spoken into a
        // focus change.
        var a = new CommandPaletteAnnouncer();
        a.Opening();
        Assert.Null(a.StatusChanged("12 commands"));
        Assert.Null(a.StatusChanged("12 commands"));
    }

    [Fact]
    public void Focused_SpeaksTheCountThatWasHeld()
    {
        var a = new CommandPaletteAnnouncer();
        a.Opening();
        a.StatusChanged("12 commands");
        Assert.Equal("12 commands", a.Focused("12 commands"));
    }

    [Fact]
    public void Opening_DoesNotBankTheHeldCountAsAlreadySpoken()
    {
        // The regression this exists for: recording the count while
        // holding it would make the identical count arriving after focus
        // look like a repeat, and the palette would open in silence.
        var a = new CommandPaletteAnnouncer();
        a.Opening();
        a.StatusChanged("12 commands");
        Assert.NotNull(a.Focused("12 commands"));
    }

    [Fact]
    public void Reopening_OnTheSameCountSpeaksItAgain()
    {
        var a = new CommandPaletteAnnouncer();
        a.Opening();
        a.Focused("12 commands");
        a.Opening();
        Assert.Equal("12 commands", a.Focused("12 commands"));
    }

    [Fact]
    public void AfterFocus_TypingResumesNormalSuppression()
    {
        var a = new CommandPaletteAnnouncer();
        a.Opening();
        a.Focused("12 commands");
        Assert.Null(a.StatusChanged("12 commands"));
        Assert.Equal("2 commands", a.StatusChanged("2 commands"));
    }

    [Fact]
    public void Focused_SaysNothingWhenTheCountIsAlreadySpoken()
    {
        // Focus can land in the search box again without a reopen (the
        // user clicks back into it). That is not new information.
        var a = new CommandPaletteAnnouncer();
        a.Opening();
        a.Focused("12 commands");
        Assert.Null(a.Focused("12 commands"));
    }

    // An announcer that has already been through an open/focus cycle, i.e.
    // one for a palette the user is typing in.
    private static CommandPaletteAnnouncer Live()
    {
        var a = new CommandPaletteAnnouncer();
        a.Opening();
        a.Focused("seed");
        return a;
    }
}
