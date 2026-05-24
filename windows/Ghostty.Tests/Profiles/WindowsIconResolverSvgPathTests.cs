using System.Text;
using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class WindowsIconResolverSvgPathTests
{
    private const string MinimalSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\">"
        + "<rect width=\"32\" height=\"32\" fill=\"green\"/></svg>";

    [Fact]
    public async System.Threading.Tasks.Task Resolve_PathWithSvgExtension_RasterizesToPng()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        fs.AddFile(@"C:\custom.svg", Encoding.UTF8.GetBytes(MinimalSvg));
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.Path(@"C:\custom.svg"), CancellationToken.None);

        Assert.NotEmpty(bytes);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_PathWithPngExtension_ReadsBytesUnchanged()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.LocalAppData, @"C:\cache");
        var raw = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xAA, 0xBB };
        fs.AddFile(@"C:\custom.png", raw);
        var resolver = new WindowsIconResolver(fs);

        var bytes = await resolver.ResolveAsync(new IconSpec.Path(@"C:\custom.png"), CancellationToken.None);

        Assert.Equal(raw, bytes);
    }
}
