using Ghostty.Core;
using Ghostty.Core.Version;
using Xunit;

namespace Ghostty.Tests.Version;

public sealed class VersionHeaderTests
{
    // Distinct numbers on purpose: the header exists so the two versions
    // cannot be read as one, and a fixture where they agree proves nothing.
    // The product name is read from AppIdentity, not spelled, because the
    // packaged flavours rebind it (Wintty Pro) and run these same tests.
    private static readonly string Product = AppIdentity.ProductName;

    private static VersionInfo Sample() => new(
        WinttyVersion:       "1.0.0-rc.1",
        BuildLabel:          "",
        WinttyVersionString: "1.0.0-rc.1-tip+abc1234",
        WinttyCommit:        "abc1234",
        Edition:             Edition.Oss,
        LibGhostty: new LibGhosttyBuildInfo(
            Version:       "1.3.2-dev",
            VersionString: "1.3.2-dev+abc1234",
            Commit:        "abc1234",
            Channel:       "tip",
            ZigVersion:    "0.16.0",
            BuildMode:     "ReleaseFast"),
        DotnetRuntime:   "10.0.0",
        MsbuildConfig:   "Release",
        AppRuntime:      "WinUI 3",
        Renderer:        "DX12",
        FontEngine:      "DirectWrite",
        WindowsVersion:  "11.0.26200",
        Architecture:    "x64");

    [Fact]
    public void Compose_BothHalves_EachUnderItsOwnPrefix()
    {
        Assert.Equal(
            $"{Product} w1.0.0-rc.1 (tip) on libghostty v1.3.2-dev",
            VersionHeader.Compose(Sample()));
    }

    [Fact]
    public void ComposeVersion_OmitsProductName()
    {
        Assert.Equal(
            "w1.0.0-rc.1 (tip) on libghostty v1.3.2-dev",
            VersionHeader.ComposeVersion(Sample()));
    }

    [Fact]
    public void Compose_StableCadence_ReadsStable()
    {
        var info = Sample() with { WinttyVersion = "1.0.0", WinttyVersionString = "1.0.0-stable+abc1234" };
        Assert.Equal(
            $"{Product} w1.0.0 (stable) on libghostty v1.3.2-dev",
            VersionHeader.Compose(info));
    }

    [Fact]
    public void Compose_NoLibghosttyVersion_OmitsThatHalf()
    {
        var sample = Sample();
        var info = sample with { LibGhostty = sample.LibGhostty with { Version = "" } };
        Assert.Equal($"{Product} w1.0.0-rc.1 (tip)", VersionHeader.Compose(info));
    }

    [Fact]
    public void Compose_UnrecoverableCadence_OmitsParens()
    {
        // A version string that does not extend the version with a cadence
        // word is rendered without a guess, not with a wrong one.
        var info = Sample() with { WinttyVersionString = "0.0.0" };
        Assert.Equal($"{Product} w1.0.0-rc.1 on libghostty v1.3.2-dev", VersionHeader.Compose(info));
    }

    [Theory]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1-tip+abc1234", "tip")]
    [InlineData("1.0.0", "1.0.0-stable+abc1234", "stable")]
    [InlineData("1.0.0", "1.0.0-stable", "stable")]
    // The shape a dirty dev tree produces: the suffix sits after '+'.
    [InlineData("0.0.0", "0.0.0-tip+abc1234-dirty", "tip")]
    // The rc identifier must not be mistaken for the cadence, whichever
    // side of the version it ends up on.
    [InlineData("1.0.0", "1.0.0-rc.1-tip+abc1234", "")]
    [InlineData("1.0.0", "1.0.0-rc.1+abc1234", "")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1+abc1234", "")]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1-tip-debug+abc1234", "")]
    [InlineData("", "1.0.0-tip+abc1234", "")]
    [InlineData("1.0.0", "", "")]
    public void Cadence_RecoversOnlyTheStampedWord(string version, string versionString, string expected)
    {
        Assert.Equal(expected, VersionHeader.Cadence(version, versionString));
    }
}
