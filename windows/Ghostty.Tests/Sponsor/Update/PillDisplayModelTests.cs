using Ghostty.Core.Sponsor.Update;
using Xunit;
using static Ghostty.Core.Sponsor.Update.PillDisplayModel;

namespace Ghostty.Tests.Sponsor.Update;

public class PillDisplayModelTests
{
    private static UpdateStateSnapshot Snap(UpdateState s, string? v = null, double? p = null, string? e = null) =>
        new(s, v, p, e, System.DateTimeOffset.UnixEpoch);

    [Fact]
    public void Idle_Hidden()
    {
        var d = MapFromState(Snap(UpdateState.Idle));
        Assert.False(d.IsVisible);
    }

    [Fact]
    public void NoUpdatesFound_Visible_NeutralBrush()
    {
        var d = MapFromState(Snap(UpdateState.NoUpdatesFound));
        Assert.True(d.IsVisible);
        Assert.Equal("SubtleFillColorSecondaryBrush", d.ThemeBrushKey);
        Assert.Equal("No Updates Found", d.Label);
    }

    [Fact]
    public void UpdateAvailable_Visible_AccentBrush_LabelHasVersion()
    {
        var d = MapFromState(Snap(UpdateState.UpdateAvailable, v: "1.4.2"));
        Assert.True(d.IsVisible);
        Assert.Equal("SystemAccentColorBrush", d.ThemeBrushKey);
        Assert.Contains("1.4.2", d.Label);
    }

    [Fact]
    public void Downloading_ShowsProgressRing_LabelHasPercent()
    {
        var d = MapFromState(Snap(UpdateState.Downloading, p: 0.42));
        Assert.True(d.IsVisible);
        Assert.True(d.ShowProgressRing);
        Assert.InRange(d.ProgressValue, 0.419, 0.421);
        Assert.Contains("42", d.Label);
    }

    [Fact]
    public void Extracting_ShowsProgressRing_IndeterminateLabel()
    {
        var d = MapFromState(Snap(UpdateState.Extracting));
        Assert.True(d.IsVisible);
        Assert.True(d.ShowProgressRing);
        Assert.Equal("Preparing update...", d.Label);
    }

    [Fact]
    public void Installing_ShowsProgressRing()
    {
        var d = MapFromState(Snap(UpdateState.Installing));
        Assert.True(d.IsVisible);
        Assert.True(d.ShowProgressRing);
        Assert.Equal("Installing update...", d.Label);
    }

    [Fact]
    public void RestartPending_Visible_AccentBrush()
    {
        var d = MapFromState(Snap(UpdateState.RestartPending));
        Assert.True(d.IsVisible);
        Assert.Equal("SystemAccentColorBrush", d.ThemeBrushKey);
        Assert.Contains("Restart", d.Label);
    }

    [Fact]
    public void Error_Visible_CautionBrush()
    {
        var d = MapFromState(Snap(UpdateState.Error, e: "boom"));
        Assert.True(d.IsVisible);
        Assert.Equal("SystemFillColorCautionBrush", d.ThemeBrushKey);
        Assert.Contains("Error", d.Label);
    }
}
