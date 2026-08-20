using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Commands;

/// <summary>
/// The palette's spoken names live in XAML, where no C# type can hold them.
/// These parse the markup rather than grep it: an attribute is only set if
/// it is an attribute of the element that needs it, and a substring match
/// would pass on a name sitting on the wrong element, inside a comment, or
/// on an element that was since renamed.
/// </summary>
public class CommandPaletteAutomationTests
{
    private const string Xaml = "Ghostty.Tests.Commands.CommandPaletteControl.xaml";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName AutomationName = "AutomationProperties.Name";

    [Fact]
    public void Palette_RootIsNamed()
    {
        var root = Markup().Root!;
        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.Equal("Command palette", (string?)root.Attribute(AutomationName));
    }

    [Fact]
    public void SearchBox_IsNamedAndKeepsItsAutomationId()
    {
        // x:Name is the AutomationId every mouse-fuzz harness locates the
        // palette by; renaming it breaks them, and it is not spoken.
        var box = Named("SearchBox");
        Assert.Equal("TextBox", box.Name.LocalName);
        Assert.Equal("Search commands", (string?)box.Attribute(AutomationName));
        // PlaceholderText is not a substitute: it is not the UIA name, and
        // it is gone from the visual once the user types.
        Assert.NotEqual(
            (string?)box.Attribute("PlaceholderText"),
            (string?)box.Attribute(AutomationName));
    }

    [Fact]
    public void ResultsList_IsNamed()
    {
        var list = Named("ResultsList");
        Assert.Equal("ListView", list.Name.LocalName);
        Assert.False(string.IsNullOrWhiteSpace((string?)list.Attribute(AutomationName)));
    }

    [Fact]
    public void PinButton_IsNamed()
    {
        // Its only content is a glyph, so without a name a reader has
        // nothing but the private-use codepoint to speak.
        var pin = Named("PinButton");
        Assert.False(string.IsNullOrWhiteSpace((string?)pin.Attribute(AutomationName)));
    }

    [Fact]
    public void ModeLabel_SeedNameMatchesItsSeedText()
    {
        // The markup carries the starting mode; code-behind rewrites both
        // on every mode change. If the two drift, the palette opens
        // announcing a mode it is not in.
        var label = Named("ModeLabel");
        var text = (string?)label.Attribute("Text");
        Assert.Equal(
            CommandPaletteAnnouncer.ModeAccessibleName(text),
            (string?)label.Attribute(AutomationName));
    }

    [Fact]
    public void Palette_HasNoSilentLiveRegion()
    {
        // A LiveSetting on its own announces nothing: WinUI raises no
        // automation event when a TextBlock's Text is assigned, so the
        // live region was dead markup. Status goes through UiaAnnouncer.
        var withLiveSetting = Markup().Descendants()
            .Where(e => e.Attribute("AutomationProperties.LiveSetting") is not null)
            .ToList();
        Assert.Empty(withLiveSetting);
        Assert.Null(Markup().Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(X + "Name") == "LiveAnnouncer"));
    }

    [Fact]
    public void CodeBehind_RoutesStatusThroughTheSharedAnnouncer()
    {
        var source = ReadEmbedded("CommandPaletteControl.xaml.cs");
        Assert.Contains("UiaAnnouncer.Announce(SearchBox, text, \"palette-status\")", source);
        // The dead live region must not come back alongside it.
        Assert.DoesNotContain("LiveAnnouncer", source);
    }

    [Fact]
    public void MainWindow_CapturesTheFocusedSurfaceByWalkingToIt()
    {
        // TerminalControl hands focus straight to its ImeSink TextBox, so
        // the focused element is never the TerminalControl and a cast to
        // it always yielded null - the palette's focus restore was dead.
        var terminal = ReadEmbedded("TerminalControl.xaml.cs");
        Assert.Contains("ImeSink.Focus(FocusState.Programmatic)", terminal);

        var main = ReadEmbedded("MainWindow.xaml.cs");
        Assert.DoesNotContain("GetFocusedElement(Content.XamlRoot) as Controls.TerminalControl", main);
        Assert.Contains("_previousFocusSurface = FocusedTerminal();", main);
        Assert.Contains("if (node is Controls.TerminalControl terminal) return terminal;", main);
    }

    private static XElement Named(string name) =>
        Markup().Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static XDocument Markup()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(Xaml);
        Assert.NotNull(stream);
        return XDocument.Load(stream!);
    }

    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
