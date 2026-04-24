using System.Linq;
using System.Threading;
using Ghostty.Core.Profiles;
using Ghostty.Core.Profiles.Probes;
using Ghostty.Tests.Profiles.Fakes;
using Xunit;

namespace Ghostty.Tests.Profiles.Probes;

public sealed class CmdProbeTests
{
    [Fact]
    public async System.Threading.Tasks.Task Discover_SystemFolderPresent_ReturnsCmdProfile()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.System, @"C:\Windows\System32");
        fs.AddFile(@"C:\Windows\System32\cmd.exe");

        var probe = new CmdProbe(fs);
        var result = await probe.DiscoverAsync(CancellationToken.None);

        var p = Assert.Single(result);
        Assert.Equal("cmd", p.Id);
        Assert.Equal("Command Prompt", p.Name);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", p.Command);
        Assert.Equal("cmd", p.ProbeId);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_SystemFolderMissing_ReturnsEmpty()
    {
        var fs = new FakeFileSystem();
        var probe = new CmdProbe(fs);
        var result = await probe.DiscoverAsync(CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discover_CmdExeMissing_ReturnsEmpty()
    {
        var fs = new FakeFileSystem();
        fs.SetKnownFolder(KnownFolderId.System, @"C:\Windows\System32");
        var probe = new CmdProbe(fs);
        var result = await probe.DiscoverAsync(CancellationToken.None);
        Assert.Empty(result);
    }
}
