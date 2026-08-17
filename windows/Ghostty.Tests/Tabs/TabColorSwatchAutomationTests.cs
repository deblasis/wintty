using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Tab color swatches only had ToolTipService.ToolTip. UIA Name stayed
/// empty, so a remaining-chrome fuzz could open Tab Color... but never
/// find "Blue" / "None" under the hwnd.
/// </summary>
public class TabColorSwatchAutomationTests
{
    [Fact]
    public void WrapSwatch_SetsAutomationNameFromLocalizedName()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("TabColorPalettePicker.xaml.cs", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();
        Assert.Contains("AutomationProperties.SetName", source);
        Assert.Contains("TabColorPalette.LocalizedName(color)", source);
    }
}
