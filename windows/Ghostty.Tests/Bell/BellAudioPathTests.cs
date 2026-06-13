using System.IO;
using Ghostty.Core.Bell;
using Xunit;

namespace Ghostty.Tests.Bell;

public class BellAudioPathTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrBlank_ReturnsNull(string? raw)
    {
        Assert.Null(BellAudioPath.Resolve(raw, configDir: @"C:\cfg", homeDir: @"C:\Users\me"));
    }

    [Fact]
    public void Resolve_AbsolutePath_ReturnedNormalized()
    {
        var result = BellAudioPath.Resolve(@"C:\sounds\bell.wav", @"C:\cfg", @"C:\Users\me");
        Assert.Equal(Path.GetFullPath(@"C:\sounds\bell.wav"), result);
    }

    [Fact]
    public void Resolve_TildeSlash_ExpandsAgainstHome()
    {
        var result = BellAudioPath.Resolve("~/bell.wav", @"C:\cfg", @"C:\Users\me");
        Assert.Equal(Path.GetFullPath(Path.Combine(@"C:\Users\me", "bell.wav")), result);
    }

    [Fact]
    public void Resolve_RelativePath_ResolvesAgainstConfigDir()
    {
        var result = BellAudioPath.Resolve("bell.wav", @"C:\cfg", @"C:\Users\me");
        Assert.Equal(Path.GetFullPath(Path.Combine(@"C:\cfg", "bell.wav")), result);
    }

    [Fact]
    public void Resolve_RelativePath_NoConfigDir_FallsBackToCwd()
    {
        var result = BellAudioPath.Resolve("bell.wav", configDir: null, homeDir: @"C:\Users\me");
        Assert.Equal(Path.GetFullPath("bell.wav"), result);
    }
}
