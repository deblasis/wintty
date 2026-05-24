using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class ProcessIconTableTests
{
    [Fact]
    public void TryMap_UnknownExe_ReturnsNull()
    {
        Assert.Null(ProcessIconTable.TryMap("never-shipped.exe"));
    }

    [Fact]
    public void TryMap_PwshExe_ReturnsBrandKey()
    {
        var spec = ProcessIconTable.TryMap("pwsh.exe");
        var brand = Assert.IsType<IconSpec.BrandKey>(spec);
        Assert.Equal("pwsh", brand.Key);
    }

    [Fact]
    public void TryMap_PwshExe_IsCaseInsensitive()
    {
        var spec = ProcessIconTable.TryMap("Pwsh.EXE");
        var brand = Assert.IsType<IconSpec.BrandKey>(spec);
        Assert.Equal("pwsh", brand.Key);
    }

    [Fact]
    public void TryMap_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(ProcessIconTable.TryMap(null!));
        Assert.Null(ProcessIconTable.TryMap(""));
    }

    [Theory]
    [InlineData("python.exe",  "python")]
    [InlineData("python3.exe", "python")]
    [InlineData("node.exe",    "node")]
    [InlineData("deno.exe",    "deno")]
    [InlineData("bun.exe",     "bun")]
    [InlineData("vim.exe",     "vim")]
    [InlineData("nvim.exe",    "vim")]
    [InlineData("git.exe",     "git")]
    [InlineData("ssh.exe",     "ssh")]
    [InlineData("docker.exe",  "docker")]
    [InlineData("kubectl.exe", "k8s")]
    [InlineData("cargo.exe",   "rust")]
    [InlineData("rustc.exe",   "rust")]
    [InlineData("dotnet.exe",  "dotnet")]
    [InlineData("go.exe",      "go")]
    [InlineData("make.exe",    "make")]
    [InlineData("htop.exe",    "monitor")]
    [InlineData("btop.exe",    "monitor")]
    [InlineData("top.exe",     "monitor")]
    public void TryMap_KnownExe_ReturnsExpectedBrand(string exe, string expectedKey)
    {
        var spec = ProcessIconTable.TryMap(exe);
        var brand = Assert.IsType<IconSpec.BrandKey>(spec);
        Assert.Equal(expectedKey, brand.Key);
    }

    [Fact]
    public void TryMap_WslHostExe_ReturnsNull()
    {
        // wslhost.exe is the WSL broker; it appears in every WSL tab's
        // descendant tree but is not what the user is interacting with.
        Assert.Null(ProcessIconTable.TryMap("wslhost.exe"));
    }
}
