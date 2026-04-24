using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class IconSpecTests
{
    [Fact]
    public void Path_HoldsFilePath()
    {
        IconSpec spec = new IconSpec.Path(@"C:\icons\pwsh.ico");
        var p = Assert.IsType<IconSpec.Path>(spec);
        Assert.Equal(@"C:\icons\pwsh.ico", p.FilePath);
    }

    [Fact]
    public void Mdl2Token_HoldsCodePoint()
    {
        IconSpec spec = new IconSpec.Mdl2Token(0xE756);
        var m = Assert.IsType<IconSpec.Mdl2Token>(spec);
        Assert.Equal(0xE756, m.CodePoint);
    }

    [Fact]
    public void BundledKey_HoldsKey()
    {
        IconSpec spec = new IconSpec.BundledKey("pwsh");
        var b = Assert.IsType<IconSpec.BundledKey>(spec);
        Assert.Equal("pwsh", b.Key);
    }

    [Fact]
    public void AutoForExe_HoldsExePath()
    {
        IconSpec spec = new IconSpec.AutoForExe(@"C:\Program Files\PowerShell\7\pwsh.exe");
        var a = Assert.IsType<IconSpec.AutoForExe>(spec);
        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", a.ExePath);
    }

    [Fact]
    public void AutoForWslDistro_HoldsDistroName()
    {
        IconSpec spec = new IconSpec.AutoForWslDistro("Ubuntu-22.04");
        var d = Assert.IsType<IconSpec.AutoForWslDistro>(spec);
        Assert.Equal("Ubuntu-22.04", d.DistroName);
    }

    [Fact]
    public void Variants_RecordEqualityHolds()
    {
        Assert.Equal(new IconSpec.Path("a"), new IconSpec.Path("a"));
        Assert.NotEqual<IconSpec>(new IconSpec.Path("a"), new IconSpec.BundledKey("a"));
    }
}
