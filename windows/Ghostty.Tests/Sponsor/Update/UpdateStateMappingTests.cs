using Ghostty.Core.Sponsor.Update;
using Ghostty.Core.Sponsor.Update.Mapping;
using Xunit;

namespace Ghostty.Tests.Sponsor.Update;

public class UpdateStateMappingTests
{
    [Fact]
    public void FromCheckResult_Null_EmitsNoUpdatesFound()
    {
        var snap = UpdateStateMapping.FromCheckResult(null);
        Assert.Equal(UpdateState.NoUpdatesFound, snap.State);
        Assert.Null(snap.TargetVersion);
    }

    [Fact]
    public void FromCheckResult_WithInfo_EmitsUpdateAvailable()
    {
        var info = new VelopackUpdateInfo("1.4.2", "https://example/notes", new object());
        var snap = UpdateStateMapping.FromCheckResult(info);
        Assert.Equal(UpdateState.UpdateAvailable, snap.State);
        Assert.Equal("1.4.2", snap.TargetVersion);
        Assert.Equal("https://example/notes", snap.ReleaseNotesUrl);
    }

    [Fact]
    public void FromDownloadProgress_ClampsAndPopulatesFields()
    {
        var snap = UpdateStateMapping.FromDownloadProgress(42, "1.4.2", "https://example/n");
        Assert.Equal(UpdateState.Downloading, snap.State);
        Assert.Equal("1.4.2", snap.TargetVersion);
        Assert.Equal(0.42, snap.Progress);
        Assert.Equal("https://example/n", snap.ReleaseNotesUrl);
    }

    [Theory]
    [InlineData(-5, 0.0)]
    [InlineData(0, 0.0)]
    [InlineData(150, 1.0)]
    public void FromDownloadProgress_ClampsPercent(int input, double expected)
    {
        var snap = UpdateStateMapping.FromDownloadProgress(input, "1.0.0", null);
        Assert.Equal(expected, snap.Progress);
    }

    [Fact]
    public void FromDownloadComplete_EmitsRestartPending()
    {
        var snap = UpdateStateMapping.FromDownloadComplete("1.4.2", "https://ex/n");
        Assert.Equal(UpdateState.RestartPending, snap.State);
        Assert.Equal("1.4.2", snap.TargetVersion);
        Assert.Equal("https://ex/n", snap.ReleaseNotesUrl);
    }

    [Fact]
    public void FromCancel_RevertsToUpdateAvailable()
    {
        var snap = UpdateStateMapping.FromCancel("1.4.2", "https://ex/n");
        Assert.Equal(UpdateState.UpdateAvailable, snap.State);
        Assert.Equal("1.4.2", snap.TargetVersion);
        Assert.Equal("https://ex/n", snap.ReleaseNotesUrl);
    }

    // InlineData can't embed internal enum values directly (CS0051 vs
    // xUnit1000 stand-off). Cast to int for transport, back to the enum
    // inside the test. Mirrors SnapZoneCatalogTests' pattern.
    [Theory]
    [InlineData((int)UpdateErrorKind.NoToken, "Sign in to check for updates.")]
    [InlineData((int)UpdateErrorKind.AuthExpired, "Sponsor session expired. Sign in again.")]
    [InlineData((int)UpdateErrorKind.NotEntitled, "This channel isn't available for your sponsorship tier.")]
    [InlineData((int)UpdateErrorKind.Offline, "Can't reach wintty.io. Check your connection.")]
    [InlineData((int)UpdateErrorKind.ServerError, "Update server is having a hiccup. Try again later.")]
    [InlineData((int)UpdateErrorKind.ManifestInvalid, "Update manifest unreadable. This is a bug - please report.")]
    [InlineData((int)UpdateErrorKind.HashMismatch, "Downloaded update didn't verify. Try again.")]
    [InlineData((int)UpdateErrorKind.ApplyFailed, "Couldn't apply the update. Try again or reinstall.")]
    public void FromError_PicksExpectedMessagePerKind(int kindRaw, string expected)
    {
        var kind = (UpdateErrorKind)kindRaw;
        var ex = new UpdateCheckException(kind, detail: "test");
        var snap = UpdateStateMapping.FromError(ex, targetVersion: "1.2.3");
        Assert.Equal(UpdateState.Error, snap.State);
        Assert.Equal(expected, snap.ErrorMessage);
        Assert.Equal("1.2.3", snap.TargetVersion);
    }

    [Fact]
    public void FromError_PopulatesTechnicalDetail()
    {
        var inner = new System.InvalidOperationException("NameResolutionFailure");
        var ex = new UpdateCheckException(UpdateErrorKind.Offline, "timeout after 10s", inner);
        var snap = UpdateStateMapping.FromError(ex, targetVersion: null);
        Assert.Contains("UpdateCheckException", snap.TechnicalDetail);
        Assert.Contains("timeout after 10s", snap.TechnicalDetail);
    }
}
