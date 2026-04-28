using Ghostty.Core.Version;
using Xunit;

namespace Ghostty.Tests.Version;

public sealed class VersionRendererTests
{
    private static VersionInfo Sample() => new(
        WinttyVersion:       "1.2.0",
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
        Architecture:    "x64");

    [Fact]
    public void RenderPlain_OssFixture_MatchesExpected()
    {
        var expected =
            "Wintty 1.2.0-tip+abc1234\n" +
            "  https://github.com/deblasis/wintty/commit/abc1234\n" +
            "\n" +
            "Version\n" +
            "  version:        1.2.0\n" +
            "  channel:        tip\n" +
            "  edition:        oss\n" +
            "\n" +
            "Build Config\n" +
            "  Zig version:    0.14.0\n" +
            "  .NET runtime:   10.0.0\n" +
            "  app runtime:    WinUI 3\n" +
            "  renderer:       DX12\n" +
            "  font engine:    DirectWrite\n" +
            "  libghostty:     1.2.0+abc1234\n" +
            "  windows:        11.0.26200\n" +
            "  arch:           x64\n" +
            "  build mode:     Release\n";

        Assert.Equal(expected, VersionRenderer.RenderPlain(Sample()));
    }

    [Fact]
    public void RenderPlain_NoCommit_OmitsHeaderUrl()
    {
        var info = Sample() with
        {
            WinttyVersionString = "1.2.0-tip+unknown",
            WinttyCommit = "unknown",
        };

        var output = VersionRenderer.RenderPlain(info);
        Assert.StartsWith("Wintty 1.2.0-tip+unknown\n\n", output);
        Assert.DoesNotContain("github.com/deblasis/wintty/commit", output);
    }

    [Fact]
    public void RenderAnsi_TtyFixture_HasOsc8HeaderHyperlink()
    {
        var output = VersionRenderer.RenderAnsi(Sample());
        Assert.Contains(
            "\x1b]8;;https://github.com/deblasis/wintty/commit/abc1234\x1b\\Wintty 1.2.0-tip+abc1234\x1b]8;;\x1b\\",
            output);
    }

    [Fact]
    public void RenderAnsi_NoCommit_OmitsOsc8()
    {
        var info = Sample() with { WinttyCommit = "unknown" };
        var output = VersionRenderer.RenderAnsi(info);
        Assert.DoesNotContain("\x1b]8", output);
    }
}
