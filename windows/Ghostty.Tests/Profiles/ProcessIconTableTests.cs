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
}
