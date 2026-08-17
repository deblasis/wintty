using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// Pins the WinUI 3 ABI workaround for keybind lists.
///
/// <c>ItemsControl.ItemsSource</c> throws ArgumentException for mixed
/// Ghostty.Core records (KeybindCategoryHeader / KeybindListItem): CsWinRT
/// cannot project them. Feeding those same types to an
/// ItemTemplateSelector then InvalidCastExceptions inside MeasureOverride.
/// The palette already uses Items.Clear+Add and a single ItemTemplate;
/// cheat sheet and the settings Keybindings page must follow it.
///
/// This scans embedded sources rather than instantiating ListView: the
/// test project is plain net10.0 and does not reference Ghostty.csproj.
/// </summary>
public class KeybindListWinUiAbiTests
{
    private static readonly Regex ItemsSourceAssign = new(
        @"\w+\.ItemsSource\s*=",
        RegexOptions.Compiled);

    private static readonly Regex TemplateSelectorBase = new(
        @":\s*DataTemplateSelector",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("CheatSheetDialog.xaml.cs")]
    [InlineData("KeybindingsPage.xaml.cs")]
    public void KeybindLists_DoNotAssignItemsSource(string file)
    {
        var source = ReadEmbeddedEndingWith(file);
        Assert.False(
            ItemsSourceAssign.IsMatch(source),
            $"{file} assigns ItemsSource; WinUI throws ArgumentException for mixed CLR row types. Use Items.Clear+Add.");
    }

    [Theory]
    [InlineData("CheatSheetDialog.xaml.cs")]
    [InlineData("KeybindingsPage.xaml.cs")]
    public void KeybindLists_DoNotUseItemTemplateSelector(string file)
    {
        var source = ReadEmbeddedEndingWith(file);
        Assert.False(
            TemplateSelectorBase.IsMatch(source),
            $"{file} subclasses DataTemplateSelector; that ABI InvalidCastExceptions in MeasureOverride for CLR row types. Use one ItemTemplate + ContainerContentChanging.");
    }

    [Fact]
    public void KeybindingsPageXaml_DoesNotSetItemTemplateSelector()
    {
        var xaml = ReadEmbeddedExact("Ghostty.Tests.Settings.Pages.KeybindingsPage.xaml");
        // Comments document the ABI trap; only a live attribute would re-break it.
        var live = XDocument.Parse(xaml);
        Assert.DoesNotContain(
            live.Descendants().Attributes(),
            a => a.Name.LocalName == "ItemTemplateSelector");
    }

    private static string ReadEmbeddedEndingWith(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return ReadEmbeddedExact(name);
    }

    private static string ReadEmbeddedExact(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
