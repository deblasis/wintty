using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class CommandRowAutomationTests
{
    [Fact]
    public void Name_IsTheTitle()
    {
        Assert.Equal("Copy to Clipboard", Row(title: "Copy to Clipboard").Name);
    }

    [Fact]
    public void HelpText_IsTheDescription()
    {
        Assert.Equal("Copy the selection", Row(description: "Copy the selection").HelpText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HelpText_IsNullWhenThereIsNoDescription(string? description)
    {
        // Null, not empty. The caller clears the property on null; an
        // empty string would be set, and UIA reads a set-but-empty
        // property as present, which on a recycled container leaves the
        // previous row's description in place.
        Assert.Null(Row(description: description).HelpText);
    }

    [Fact]
    public void AcceleratorKey_IsTheFormattedShortcut()
    {
        Assert.Equal("Ctrl+Shift+P", Row(shortcut: "Ctrl+Shift+P").AcceleratorKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AcceleratorKey_IsNullWhenTheCommandHasNoBinding(string? shortcut)
    {
        Assert.Null(Row(shortcut: shortcut).AcceleratorKey);
    }

    [Fact]
    public void ShortcutIsNotFoldedIntoTheName()
    {
        // It belongs on AcceleratorKey; repeating it in the name is what
        // makes every row long to listen to.
        var row = Row(title: "Copy to Clipboard", shortcut: "Ctrl+Shift+C");
        Assert.DoesNotContain("Ctrl", row.Name);
    }

    [Fact]
    public void DescriptionIsNotFoldedIntoTheName()
    {
        var row = Row(title: "Copy to Clipboard", description: "Copy the selection");
        Assert.Equal("Copy to Clipboard", row.Name);
    }

    private static CommandRowAutomation Row(
        string? title = "Some Command",
        string? description = null,
        string? shortcut = null)
        => CommandRowAutomation.For(title, description, shortcut);
}
