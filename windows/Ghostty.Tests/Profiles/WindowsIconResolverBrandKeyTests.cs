using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class WindowsIconResolverBrandKeyTests
{
    [Fact(Skip = "awaits PR A3 asset bundle")]
    public async System.Threading.Tasks.Task Resolve_BrandKey_WithExplicitDpi_ReturnsThatVariant()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.BrandKey("default", 16), CancellationToken.None);

        Assert.NotEmpty(bytes);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
    }

    [Fact(Skip = "awaits PR A3 asset bundle")]
    public async System.Threading.Tasks.Task Resolve_BrandKey_WithNullDpi_PicksADefault()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.BrandKey("default", null), CancellationToken.None);

        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_BrandKey_UnknownKey_FallsBackToDefault()
    {
        // Existing default.png (single-size, legacy) provides a fallback even
        // before the @<dpi>.png bundle lands in PR A3.
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.BrandKey("never-exists-xyz", 16), CancellationToken.None);

        Assert.NotEmpty(bytes);
    }
}
