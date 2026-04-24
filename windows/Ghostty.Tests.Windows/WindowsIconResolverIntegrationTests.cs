using System.IO;
using System.Threading;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Windows;

public sealed class WindowsIconResolverIntegrationTests
{
    [Fact]
    public async System.Threading.Tasks.Task Resolve_AutoForExe_NotepadIconPng()
    {
        var fs = new WindowsFileSystem();
        var system = fs.GetKnownFolder(KnownFolderId.System)!;
        var notepad = Path.Combine(system, "notepad.exe");
        Assert.True(fs.FileExists(notepad));

        var resolver = new WindowsIconResolver(fs);
        var bytes = await resolver.ResolveAsync(
            new IconSpec.AutoForExe(notepad),
            CancellationToken.None);

        Assert.NotEmpty(bytes);
        Assert.Equal(0x89, bytes[0]); // PNG signature
        Assert.Equal(0x50, bytes[1]);
    }

    [Fact]
    public async System.Threading.Tasks.Task Resolve_SecondCall_HitsCache()
    {
        var fs = new WindowsFileSystem();
        var system = fs.GetKnownFolder(KnownFolderId.System)!;
        var notepad = Path.Combine(system, "notepad.exe");

        var resolver = new WindowsIconResolver(fs);
        var first = await resolver.ResolveAsync(new IconSpec.AutoForExe(notepad), CancellationToken.None);
        var second = await resolver.ResolveAsync(new IconSpec.AutoForExe(notepad), CancellationToken.None);

        Assert.Equal(first, second);
    }
}
