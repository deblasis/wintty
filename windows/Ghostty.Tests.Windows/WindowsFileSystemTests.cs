using System.IO;
using System.Threading;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Windows;

public sealed class WindowsFileSystemTests
{
    [Fact]
    public void GetKnownFolder_System_ReturnsWindowsSystem32()
    {
        var fs = new WindowsFileSystem();
        var system = fs.GetKnownFolder(KnownFolderId.System);
        Assert.NotNull(system);
        Assert.True(Directory.Exists(system));
        Assert.EndsWith("System32", system!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileExists_KernelDll_ReturnsTrue()
    {
        var fs = new WindowsFileSystem();
        var system = fs.GetKnownFolder(KnownFolderId.System)!;
        Assert.True(fs.FileExists(Path.Combine(system, "kernel32.dll")));
    }

    [Fact]
    public void FileExists_NonexistentPath_ReturnsFalse()
    {
        var fs = new WindowsFileSystem();
        Assert.False(fs.FileExists(@"C:\definitely_not_a_real_path_xyz\nope.bin"));
    }
}
