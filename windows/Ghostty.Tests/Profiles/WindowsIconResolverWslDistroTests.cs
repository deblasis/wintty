using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class WindowsIconResolverWslDistroTests
{
    [Theory]
    [InlineData("Ubuntu", "ubuntu")]
    [InlineData("Ubuntu-22.04", "ubuntu")]
    [InlineData("Debian", "debian")]
    [InlineData("kali-linux", "kali")]
    [InlineData("Alpine", "alpine")]
    [InlineData("Fedora", "fedora")]
    [InlineData("openSUSE-Leap-15.5", "opensuse")]
    [InlineData("Arch", "arch")]
    public async System.Threading.Tasks.Task Resolve_WslDistro_KnownDistro_ResolvesToBrandKey(string distroName, string expectedKey)
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.AutoForWslDistro(distroName), CancellationToken.None);
        var expected = await resolver.ResolveAsync(new IconSpec.BrandKey(expectedKey, 32), CancellationToken.None);

        Assert.Equal(expected, bytes);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_WslDistro_UnknownDistro_FallsBackToLegacyWsl()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.AutoForWslDistro("Nixos"), CancellationToken.None);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_WslDistro_EmptyDistro_FallsBackThroughResolver()
    {
        // When distro name is empty, the resolver consults the registry for
        // the default distro. The test machine may or may not have WSL
        // configured: if it does, we get the matching brand; if not, we
        // fall back to the legacy wsl.png. Either way the result is a
        // non-empty PNG, which is the user-visible contract.
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(
            new IconSpec.AutoForWslDistro(string.Empty),
            CancellationToken.None);

        Assert.NotEmpty(bytes);
        // PNG signature: 89 50 4E 47.
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
    }
}
