using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Expanded vertical-tab rows expose a named close button via
/// <c>VerticalTabNavRow</c>.
/// </summary>
public class StripCloseAutomationTests
{
    [Fact]
    public void NavRowCloseButton_HasAccessibleName()
    {
        var source = ReadEmbedded("VerticalTabNavRow.cs");
        Assert.Contains("AutomationProperties.SetName(_close, \"Close tab\")", source);
        Assert.Contains("ToolTipService.SetToolTip(_close, \"Close tab\")", source);
    }

    /// <summary>
    /// The row class is only worth anything if the strip actually builds
    /// rows with it. It shipped once as a NavigationViewItem.Content of
    /// bare title text, which silently dropped the close button and the
    /// bell badge while this file's assertions still passed.
    /// </summary>
    [Fact]
    public void Strip_BuildsRowsWithNavRow_NotBareText()
    {
        var strip = ReadEmbedded("VerticalTabStrip.xaml.cs");
        Assert.Contains("new VerticalTabNavRow(", strip);
        Assert.Contains("Content = row", strip);
        Assert.DoesNotContain("Content = tab.EffectiveTitle", strip);
    }

    /// <summary>
    /// The close button is inert unless the strip raises the event the
    /// host subscribes to.
    /// </summary>
    [Fact]
    public void Strip_RaisesCloseRequestedFromRow()
    {
        var strip = ReadEmbedded("VerticalTabStrip.xaml.cs");
        Assert.Contains("CloseRequestedFromRow?.Invoke(", strip);
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
