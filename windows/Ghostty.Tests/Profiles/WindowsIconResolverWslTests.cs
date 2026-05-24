using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class WindowsIconResolverWslTests
{
    [Fact]
    public async System.Threading.Tasks.Task Resolve_AutoForWslDistro_UnknownDistro_FallsBackToBundledWsl()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");

        var resolver = new WindowsIconResolver(fs);
        // "Nixos" is not in the brand-key whitelist; resolver must fall back
        // to the legacy single-color wsl.png rather than the default icon.
        var bytes = await resolver.ResolveAsync(
            new IconSpec.AutoForWslDistro("Nixos"),
            CancellationToken.None);

        Assert.NotEmpty(bytes);
        var bundled = await resolver.ResolveAsync(
            new IconSpec.BundledKey("wsl"),
            CancellationToken.None);
        Assert.Equal(bundled, bytes);
    }
}
