using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Ghostty.Core.Accessibility;
using Ghostty.Tests.Wiring;
using Xunit;

namespace Ghostty.Tests.Commands;

/// <summary>
/// The palette's spoken names live in XAML, where no C# type can hold
/// them. These parse the markup rather than search it: an attribute is
/// only set if it is an attribute of the element that needs it, and a
/// substring match would pass on a name sitting on the wrong element,
/// inside a comment, or on an element that was since renamed.
/// </summary>
public class CommandPaletteAutomationTests
{
    private const string Xaml = "Ghostty.Tests.Commands.CommandPaletteControl.xaml";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName AutomationName = "AutomationProperties.Name";
    private static readonly XName LiveSetting = "AutomationProperties.LiveSetting";

    [Fact]
    public void Palette_RootIsNamed()
    {
        var root = Markup().Root!;
        Assert.Equal("UserControl", root.Name.LocalName);
        Assert.False(string.IsNullOrWhiteSpace((string?)root.Attribute(AutomationName)));
    }

    [Fact]
    public void Palette_RootNameIsNotACasingVariantOfTheMenuItem()
    {
        // The pane context menu offers "Command Palette", and four fuzz
        // harnesses invoke it by that name. Find-by-name is case-sensitive
        // today, so a root named "Command palette" only happens not to
        // collide; a name that differs by more than casing does not have
        // to rely on that.
        var name = (string?)Markup().Root!.Attribute(AutomationName);
        Assert.NotEqual("Command Palette", name, StringComparer.OrdinalIgnoreCase);
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
    public void StatusLabel_IsTheLiveRegion()
    {
        // The footer count is the only feedback that a query matched
        // nothing, and it has to reach a reader whose focus never leaves
        // the search box.
        var status = Named("StatusLabel");
        Assert.Equal("Polite", (string?)status.Attribute(LiveSetting));
    }

    [Fact]
    public void EveryLiveRegion_HasSomethingRaisingItsChange()
    {
        // The defect this replaces: a LiveSetting was declared on a hidden
        // TextBlock and nothing ever raised LiveRegionChanged for it, so
        // assigning its text announced precisely nothing. Live regions are
        // not banned; silent ones are.
        var code = ShellSource.Load("Controls.CommandPalette.CommandPaletteControl.xaml.cs");
        var raised = code.Method("PublishStatus")
            .Calls("UiaAnnouncer.RaiseLiveRegionChanged")
            .Select(c => c.Arg(0))
            .ToHashSet(StringComparer.Ordinal);

        var declared = LiveRegions();
        Assert.NotEmpty(declared);
        foreach (var element in declared)
        {
            var name = (string?)element.Attribute(X + "Name");
            Assert.False(
                string.IsNullOrEmpty(name),
                $"a <{element.Name.LocalName}> declares LiveSetting with no x:Name, so no code can raise it");
            Assert.Contains(name!, raised);
        }
    }

    private static List<XElement> LiveRegions() =>
        Markup().Descendants().Where(e => e.Attribute(LiveSetting) is not null).ToList();

    private static XElement Named(string name) =>
        Markup().Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static XDocument Markup()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(Xaml);
        Assert.NotNull(stream);
        return XDocument.Load(stream!);
    }
}
