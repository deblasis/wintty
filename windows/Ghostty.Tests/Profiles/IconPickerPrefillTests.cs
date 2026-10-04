using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

/// <summary>
/// "ICON PICKER DEAD PREFILL - Settings/IconPickerDialog.xaml.cs ~17
/// declares InitialSpec; ProfileIconPickerControl.xaml.cs ~62 sets it; no
/// other reference (grep-verified). The Change dialog never shows the
/// current icon. Prefill the bundled row / fields from InitialSpec."
///
/// The mapping from a profile's icon to what the picker shows lives in
/// <see cref="IconPickerPrefill"/>, and these are its cases. That the
/// dialog applies it is <c>IconPickerPrefillWiringTests</c>'s job: WinUI
/// cannot be loaded here.
/// </summary>
public class IconPickerPrefillTests
{
    [Fact]
    public void NoIcon_PrefillsNothing()
    {
        // A profile whose icon is the resolver's fallback has nothing to
        // show, and must not be handed a row to "keep".
        var prefill = IconPickerPrefill.FromSpec(null);

        Assert.Equal(IconPickerTab.Bundled, prefill.Tab);
        Assert.Null(prefill.BundledKey);
        Assert.Null(prefill.Mdl2Text);
        Assert.Null(prefill.FilePath);
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("ubuntu")]
    [InlineData("default")]
    public void ABundledKey_SelectsThatRow(string key)
    {
        var prefill = IconPickerPrefill.FromSpec(new IconSpec.BundledKey(key));

        Assert.Equal(IconPickerTab.Bundled, prefill.Tab);
        Assert.Equal(key, prefill.BundledKey);
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("ubuntu")]
    [InlineData("git")]
    public void ABrandKey_SelectsTheRowOfThatKey(string key)
    {
        // The exe table's keys ("git", "dotnet") are not all rows the grid
        // carries. The key travels as the key, not as an index: the dialog
        // decides whether there is a row to select, and this only has to
        // hand it the name.
        var prefill = IconPickerPrefill.FromSpec(new IconSpec.BrandKey(key, 32));

        Assert.Equal(IconPickerTab.Bundled, prefill.Tab);
        Assert.Equal(key, prefill.BundledKey);
    }

    [Fact]
    public void ABundledKey_KeepsTheKeyItWasGiven_NotTheCasing()
    {
        // The resolver's exe table is case-insensitive, so a spec can carry
        // "PowerShell"; the grid's rows are lowercase and the dialog's
        // lookup is OrdinalIgnoreCase to match.
        var prefill = IconPickerPrefill.FromSpec(new IconSpec.BrandKey("PowerShell", null));

        Assert.Equal("PowerShell", prefill.BundledKey);
    }

    [Theory]
    [InlineData(0xE756, "E756")]
    [InlineData(0xF8FF, "F8FF")]
    [InlineData(0x0A, "000A")]
    public void AnMdl2Token_PrefillsTheBoxWithFourHexDigits(int codePoint, string expected)
    {
        var prefill = IconPickerPrefill.FromSpec(new IconSpec.Mdl2Token(codePoint));

        Assert.Equal(IconPickerTab.Mdl2, prefill.Tab);
        Assert.Equal(expected, prefill.Mdl2Text);
        Assert.Null(prefill.BundledKey);
    }

    [Fact]
    public void APath_NamesTheFileOnTheFileTab()
    {
        var prefill = IconPickerPrefill.FromSpec(new IconSpec.Path(@"C:\icons\pwsh.ico"));

        Assert.Equal(IconPickerTab.FromFile, prefill.Tab);
        Assert.Equal(@"C:\icons\pwsh.ico", prefill.FilePath);
        Assert.Null(prefill.BundledKey);
    }

    [Fact]
    public void APath_TabCarriesNoOtherFields()
    {
        // One icon, one field. A prefill that populated both would have the
        // dialog's grid row and the file label disagreeing about the
        // profile, and OK would commit whichever one ran last.
        var prefill = IconPickerPrefill.FromSpec(new IconSpec.Path(@"C:\icons\pwsh.ico"));

        Assert.Null(prefill.Mdl2Text);
    }

    [Fact]
    public void APath_KeepsAPathThatIsNotAnIconTheGridKnows()
    {
        // A .svg path resolves at render time and is written back verbatim,
        // so the label has to show it exactly, not a best-effort match.
        var prefill = IconPickerPrefill.FromSpec(
            new IconSpec.Path(@"C:\icons\custom with spaces.svg"));

        Assert.Equal(@"C:\icons\custom with spaces.svg", prefill.FilePath);
    }

    [Theory]
    [InlineData("pwsh.exe")]
    [InlineData("Ubuntu-24.04")]
    public void AResolverChosenSpec_HasNothingToShow(string argument)
    {
        // AutoForExe / AutoForWslDistro are what the resolver picks for
        // itself; no config value spells them, so there is no row, box or
        // path to prefill. Falling through to None is the answer, not a
        // guess at a row.
        IconSpec spec = argument.EndsWith(".exe", System.StringComparison.Ordinal)
            ? new IconSpec.AutoForExe(argument)
            : new IconSpec.AutoForWslDistro(argument);

        Assert.Equal(IconPickerTab.Bundled, IconPickerPrefill.FromSpec(spec).Tab);
        Assert.Null(IconPickerPrefill.FromSpec(spec).BundledKey);
    }
}