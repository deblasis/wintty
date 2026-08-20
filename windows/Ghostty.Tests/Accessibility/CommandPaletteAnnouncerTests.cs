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

    [Fact]
    public void StatusChanged_SpeaksTheFirstCount()
    {
        var a = new CommandPaletteAnnouncer();
        Assert.Equal("12 commands", a.StatusChanged("12 commands"));
    }

    [Fact]
    public void StatusChanged_DropsAnUnchangedCount()
    {
        var a = new CommandPaletteAnnouncer();
        a.StatusChanged("12 commands");
        Assert.Null(a.StatusChanged("12 commands"));
    }

    [Fact]
    public void StatusChanged_SpeaksAgainWhenTheCountMoves()
    {
        var a = new CommandPaletteAnnouncer();
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
        var a = new CommandPaletteAnnouncer();
        a.StatusChanged("3 commands");
        Assert.Equal("3 actions", a.StatusChanged("3 actions"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void StatusChanged_SaysNothingForAnEmptyStatus(string? status)
    {
        var a = new CommandPaletteAnnouncer();
        Assert.Null(a.StatusChanged(status));
    }

    [Fact]
    public void StatusChanged_EmptyStatusDoesNotPoisonTheNextRealOne()
    {
        var a = new CommandPaletteAnnouncer();
        a.StatusChanged("5 commands");
        a.StatusChanged("");
        // The blank was never spoken, so the count that follows it is only
        // a repeat if it repeats what was actually spoken.
        Assert.Null(a.StatusChanged("5 commands"));
        Assert.Equal("6 commands", a.StatusChanged("6 commands"));
    }

    [Fact]
    public void Reset_MakesAReopenSpeakTheSameCountAgain()
    {
        var a = new CommandPaletteAnnouncer();
        a.StatusChanged("12 commands");
        a.Reset();
        Assert.Equal("12 commands", a.StatusChanged("12 commands"));
    }
}
