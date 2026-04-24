using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class WindowsIconResolverWslTests
{
    [Fact]
    public async System.Threading.Tasks.Task Resolve_AutoForWslDistro_UsesBundledWsl()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");

        var resolver = new WindowsIconResolver(fs);
        var bytes = await resolver.ResolveAsync(
            new IconSpec.AutoForWslDistro("Ubuntu-22.04"),
            CancellationToken.None);

        Assert.NotEmpty(bytes);
        // Must equal bundled wsl.png content since no per-distro probe path exists yet.
        var bundled = await resolver.ResolveAsync(
            new IconSpec.BundledKey("wsl"),
            CancellationToken.None);
        Assert.Equal(bundled, bytes);
    }
}
