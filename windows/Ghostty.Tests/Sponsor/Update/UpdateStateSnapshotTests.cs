using Ghostty.Core.Sponsor.Update;
using Xunit;

namespace Ghostty.Tests.Sponsor.Update;

public class UpdateStateSnapshotTests
{
    [Fact]
    public void Equality_IdenticalSnapshots_AreEqual()
    {
        var ts = System.DateTimeOffset.UnixEpoch;
        var a = new UpdateStateSnapshot(UpdateState.UpdateAvailable, "1.4.2", null, null, ts);
        var b = new UpdateStateSnapshot(UpdateState.UpdateAvailable, "1.4.2", null, null, ts);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentStateValues_AreNotEqual()
    {
        var ts = System.DateTimeOffset.UnixEpoch;
        var a = new UpdateStateSnapshot(UpdateState.UpdateAvailable, "1.4.2", null, null, ts);
        var b = new UpdateStateSnapshot(UpdateState.Downloading, "1.4.2", 0.5, null, ts);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Idle_HelperProducesIdleState()
    {
        var s = UpdateStateSnapshot.Idle();
        Assert.Equal(UpdateState.Idle, s.State);
        Assert.Null(s.TargetVersion);
        Assert.Null(s.Progress);
        Assert.Null(s.ErrorMessage);
    }

    [Fact]
    public void TechnicalDetail_DefaultsToNull()
    {
        var snap = UpdateStateSnapshot.Idle();
        Assert.Null(snap.TechnicalDetail);
    }

    [Fact]
    public void TechnicalDetail_InitSetterProducesExpectedValue()
    {
        var ts = System.DateTimeOffset.UnixEpoch;
        var snap = new UpdateStateSnapshot(UpdateState.Error, null, null, "boom", ts)
        {
            TechnicalDetail = "HttpRequestException: NameResolutionFailure",
        };
        Assert.Equal("HttpRequestException: NameResolutionFailure", snap.TechnicalDetail);
    }

    [Fact]
    public void TechnicalDetail_ParticipatesInEquality()
    {
        var ts = System.DateTimeOffset.UnixEpoch;
        var a = new UpdateStateSnapshot(UpdateState.Error, null, null, "e", ts) { TechnicalDetail = "x" };
        var b = new UpdateStateSnapshot(UpdateState.Error, null, null, "e", ts) { TechnicalDetail = "x" };
        var c = new UpdateStateSnapshot(UpdateState.Error, null, null, "e", ts) { TechnicalDetail = "y" };
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
