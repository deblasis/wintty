using Ghostty.Core.Version;
using Xunit;

namespace Ghostty.Tests.Version;

/// <summary>
/// The support dump's animations field: one line, coded in public
/// vocabulary ("full" / "reduced" / "off"), inside the Build Config
/// block, and absent entirely when the caller could not resolve a state.
/// Absent, not empty: a dump that cannot know must not print a blank
/// answer.
/// </summary>
public sealed class VersionRendererAnimationsTests
{
    private static VersionInfo Sample(string? animations = null) => new(
        WinttyVersion:       "1.2.0",
        BuildLabel:          "",
        WinttyVersionString: "1.2.0-tip+abc1234",
        WinttyCommit:        "abc1234",
        Edition:             Edition.Oss,
        LibGhostty: new LibGhosttyBuildInfo(
            Version:       "1.2.0",
            VersionString: "1.2.0-tip+abc1234",
            Commit:        "abc1234",
            Channel:       "tip",
            ZigVersion:    "0.14.0",
            BuildMode:     "ReleaseFast"),
        DotnetRuntime:   "10.0.0",
        MsbuildConfig:   "Release",
        AppRuntime:      "WinUI 3",
        Renderer:        "DX12",
        FontEngine:      "DirectWrite",
        WindowsVersion:  "11.0.26200",
        Architecture:    "x64",
        Animations:      animations);

    [Theory]
    [InlineData("full")]
    [InlineData("reduced")]
    [InlineData("off")]
    public void The_dump_carries_the_coded_state_exactly_once(string code)
    {
        var output = VersionRenderer.RenderPlainBody(Sample(code));

        Assert.Equal(
            1,
            output.Split('\n').Count(l => l.StartsWith("  animations:", System.StringComparison.Ordinal)));
        Assert.Contains("  animations:     " + code + "\n", output, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_field_reports_in_the_build_config_block()
    {
        var output = VersionRenderer.RenderPlainBody(Sample("full"));

        Assert.True(
            output.IndexOf("Build Config\n", System.StringComparison.Ordinal)
            < output.IndexOf("  animations:", System.StringComparison.Ordinal),
            "the animations line belongs with the runtime probes in Build Config");
    }

    [Fact]
    public void A_dump_without_the_field_omits_the_line_entirely()
    {
        // The default (+version, which never reads config) passes nothing:
        // the line must be absent, not empty.
        Assert.DoesNotContain("animations:", VersionRenderer.RenderPlain(Sample()));
        Assert.DoesNotContain("animations:", VersionRenderer.RenderAnsi(Sample()));
        Assert.DoesNotContain("animations:", VersionRenderer.RenderPlainBody(Sample()));
    }
}
