using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class WindowsIconResolverBundledKeyTests
{
    [Fact]
    public async System.Threading.Tasks.Task Resolve_KnownBundledKey_ReturnsNonEmptyPng()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.BundledKey("cmd"), CancellationToken.None);

        Assert.NotEmpty(bytes);
        // Real PNG signature: 89 50 4E 47.
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_UnknownBundledKey_FallsBackToDefault()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.BundledKey("nonexistent"), CancellationToken.None);

        Assert.NotEmpty(bytes); // fell back to default.png
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_PathSpec_ReadsFileBytes()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var iconBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        fs.AddFile(@"C:\myicon.png", iconBytes);

        var resolver = new WindowsIconResolver(fs);
        var bytes = await resolver.ResolveAsync(new IconSpec.Path(@"C:\myicon.png"), CancellationToken.None);

        Assert.Equal(iconBytes, bytes);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_BundledKey_WritesThroughToCache()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.BundledKey("cmd"), CancellationToken.None);

        // After a successful uncached resolve, the bytes must be parked
        // under IconCache\<sha>.png so subsequent resolves can short-circuit.
        var cacheFile = System.Linq.Enumerable.Single(
            fs.EnumerateKeys(),
            k => k.StartsWith(@"C:\cache\Wintty\IconCache\", System.StringComparison.Ordinal));
        Assert.Equal(bytes, fs.ReadAllBytesSync(cacheFile));
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_PrepopulatedCache_ShortCircuitsToCachedBytes()
    {
        // Prove the cache-read path: pre-populate the exact SHA'd cache
        // file for BundledKey("cmd") with sentinel bytes; the resolver
        // must return those verbatim instead of reading the manifest PNG.
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var first = new WindowsIconResolver(fs);
        _ = await first.ResolveAsync(new IconSpec.BundledKey("cmd"), CancellationToken.None);
        var cachePath = System.Linq.Enumerable.Single(
            fs.EnumerateKeys(),
            k => k.StartsWith(@"C:\cache\Wintty\IconCache\", System.StringComparison.Ordinal));

        var sentinel = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xAA, 0xBB, 0xCC, 0xDD };
        fs.AddFile(cachePath, sentinel);

        var second = new WindowsIconResolver(fs);
        var bytes = await second.ResolveAsync(new IconSpec.BundledKey("cmd"), CancellationToken.None);

        Assert.Equal(sentinel, bytes);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_PathSpec_MtimeChangeInvalidatesCachedPng()
    {
        // A user upgrading their custom .ico (same path, newer mtime) must
        // see the new bytes. Cache key folds the mtime in so the two
        // resolves land on different SHAs.
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var iconV1 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01 };
        fs.AddFile(@"C:\myicon.png", iconV1);
        fs.SetLastWriteTimeUtc(@"C:\myicon.png", new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));

        var resolver = new WindowsIconResolver(fs);
        var first = await resolver.ResolveAsync(new IconSpec.Path(@"C:\myicon.png"), CancellationToken.None);
        Assert.Equal(iconV1, first);

        // User overwrites the .ico with new bytes and the filesystem bumps mtime.
        var iconV2 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x02 };
        fs.AddFile(@"C:\myicon.png", iconV2);
        fs.SetLastWriteTimeUtc(@"C:\myicon.png", new System.DateTime(2026, 2, 1, 0, 0, 0, System.DateTimeKind.Utc));

        var second = await resolver.ResolveAsync(new IconSpec.Path(@"C:\myicon.png"), CancellationToken.None);
        Assert.Equal(iconV2, second);
    }
}
