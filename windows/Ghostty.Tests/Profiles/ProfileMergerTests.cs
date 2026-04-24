using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class ProfileMergerTests
{
    private static EffectiveVisualOverrides Base(
        string? theme = null,
        double? opacity = null,
        string? font = null,
        double? size = null,
        string? cursor = null)
        => new(theme, opacity, font, size, cursor);

    [Fact]
    public void Merge_NoProfileOverrides_ReturnsBaseAsIs()
    {
        var baseV = Base(theme: "Light", opacity: 1.0, font: "Cascadia", size: 12, cursor: "block");

        var merged = ProfileMerger.Merge(baseV, EffectiveVisualOverrides.Empty);

        Assert.Equal(baseV, merged);
    }

    [Fact]
    public void Merge_ProfileOverridesEachKey_ProfileWins()
    {
        var baseV = Base(theme: "Light", opacity: 1.0, font: "Cascadia", size: 12, cursor: "block");
        var profile = Base(theme: "Dark", opacity: 0.85, font: "FiraCode", size: 14, cursor: "bar");

        var merged = ProfileMerger.Merge(baseV, profile);

        Assert.Equal("Dark", merged.Theme);
        Assert.Equal(0.85, merged.BackgroundOpacity);
        Assert.Equal("FiraCode", merged.FontFamily);
        Assert.Equal(14, merged.FontSize);
        Assert.Equal("bar", merged.CursorStyle);
    }

    [Fact]
    public void Merge_ProfilePartialOverride_NullKeysInheritFromBase()
    {
        var baseV = Base(theme: "Light", opacity: 1.0, font: "Cascadia", size: 12, cursor: "block");
        var profile = Base(opacity: 0.5);

        var merged = ProfileMerger.Merge(baseV, profile);

        Assert.Equal("Light", merged.Theme);
        Assert.Equal(0.5, merged.BackgroundOpacity);
        Assert.Equal("Cascadia", merged.FontFamily);
        Assert.Equal(12, merged.FontSize);
        Assert.Equal("block", merged.CursorStyle);
    }

    [Fact]
    public void Merge_ProfileEmpty_BaseUnchanged()
    {
        var baseV = Base(theme: "X", opacity: 0.9);
        var merged = ProfileMerger.Merge(baseV, EffectiveVisualOverrides.Empty);
        Assert.Equal(baseV, merged);
    }

    [Fact]
    public void Merge_BothEmpty_AllNulls()
    {
        var merged = ProfileMerger.Merge(EffectiveVisualOverrides.Empty, EffectiveVisualOverrides.Empty);

        Assert.Null(merged.Theme);
        Assert.Null(merged.BackgroundOpacity);
        Assert.Null(merged.FontFamily);
        Assert.Null(merged.FontSize);
        Assert.Null(merged.CursorStyle);
    }
}
