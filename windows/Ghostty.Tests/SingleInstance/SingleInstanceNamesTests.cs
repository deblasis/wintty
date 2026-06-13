using Ghostty.Core.SingleInstance;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

public sealed class SingleInstanceNamesTests
{
    [Fact]
    public void SamePath_ProducesStableNames()
    {
        var a = SingleInstanceNames.For(@"C:\Program Files\Wintty\Wintty.exe");
        var b = SingleInstanceNames.For(@"C:\Program Files\Wintty\Wintty.exe");
        Assert.Equal(a.Mutex, b.Mutex);
        Assert.Equal(a.Pipe, b.Pipe);
    }

    [Fact]
    public void PathIsCaseAndSeparatorInsensitive()
    {
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty\Wintty.exe");
        var b = SingleInstanceNames.For(@"c:/apps/wintty/wintty.exe");
        Assert.Equal(a.Mutex, b.Mutex);
        Assert.Equal(a.Pipe, b.Pipe);
    }

    [Fact]
    public void DifferentPaths_ProduceDifferentNames()
    {
        var a = SingleInstanceNames.For(@"C:\A\Wintty.exe");
        var b = SingleInstanceNames.For(@"C:\B\Wintty.exe");
        Assert.NotEqual(a.Mutex, b.Mutex);
        Assert.NotEqual(a.Pipe, b.Pipe);
    }

    [Fact]
    public void MutexIsSessionLocalScoped()
    {
        var n = SingleInstanceNames.For(@"C:\X\Wintty.exe");
        Assert.StartsWith(@"Local\", n.Mutex);
        // Pipe names live under \\.\pipe\ and must not contain backslashes
        // in the user-supplied portion.
        Assert.DoesNotContain(@"\", n.Pipe);
    }
}
