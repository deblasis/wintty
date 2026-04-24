using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Windows;

public sealed class WindowsRegistryReaderTests
{
    [Fact]
    public void KeyExists_HkcuSoftware_True()
    {
        var reg = new WindowsRegistryReader();
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, @"Software"));
    }

    [Fact]
    public void KeyExists_Nonexistent_False()
    {
        var reg = new WindowsRegistryReader();
        Assert.False(reg.KeyExists(RegistryHive.LocalMachine,
            @"SOFTWARE\Definitely\Not\A\Real\Key\Xyz"));
    }

    [Fact]
    public void ReadString_MissingValue_ReturnsNull()
    {
        var reg = new WindowsRegistryReader();
        var v = reg.ReadString(RegistryHive.CurrentUser, @"Software", "NoSuchValueName");
        Assert.Null(v);
    }
}
