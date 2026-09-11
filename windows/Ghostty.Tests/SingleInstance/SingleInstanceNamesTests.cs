using Ghostty.Core.SingleInstance;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

public sealed class SingleInstanceNamesTests
{
    // The fork's oss edition, and a tier build's Pro one: distinct AUMIDs,
    // which is the only per-variant input a pack build carries.
    private const string OssEdition = "com.deblasis.wintty";
    private const string ProEdition = "com.deblasis.wintty.pro";

    [Fact]
    public void SamePathAndEdition_ProducesStableNames()
    {
        var a = SingleInstanceNames.For(@"C:\Program Files\Wintty\Wintty.exe", OssEdition, null);
        var b = SingleInstanceNames.For(@"C:\Program Files\Wintty\Wintty.exe", OssEdition, null);
        Assert.Equal(a.Mutex, b.Mutex);
        Assert.Equal(a.Pipe, b.Pipe);
    }

    [Fact]
    public void PathIsCaseAndSeparatorInsensitive()
    {
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty\Wintty.exe", OssEdition, null);
        var b = SingleInstanceNames.For(@"c:/apps/wintty/wintty.exe", OssEdition, null);
        Assert.Equal(a.Mutex, b.Mutex);
        Assert.Equal(a.Pipe, b.Pipe);
    }

    [Fact]
    public void DifferentPaths_SameEdition_ProduceDifferentNames()
    {
        // A dev tree beside an installed app is two installs of one edition;
        // the dev launch must not hand itself to the installed primary.
        var a = SingleInstanceNames.For(@"C:\A\Wintty.exe", OssEdition, null);
        var b = SingleInstanceNames.For(@"C:\B\Wintty.exe", OssEdition, null);
        Assert.NotEqual(a.Mutex, b.Mutex);
        Assert.NotEqual(a.Pipe, b.Pipe);
    }

    /// <summary>
    /// The sideloading story (#1094): Wintty and Wintty Pro are separate
    /// apps and must run side by side, so the edition id is part of the
    /// identity even when the exe path alone could not tell them apart.
    /// </summary>
    [Fact]
    public void DifferentEditions_SamePath_ProduceDifferentNames()
    {
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, null);
        var b = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", ProEdition, null);
        Assert.NotEqual(a.Mutex, b.Mutex);
        Assert.NotEqual(a.Pipe, b.Pipe);
    }

    /// <summary>
    /// The test isolation story (#1094): every harness arms the marker over
    /// its own random config root, and two different roots must be two
    /// identities or a test launch forwards into another harness's app.
    /// </summary>
    [Fact]
    public void DifferentTestRoots_SamePathAndEdition_ProduceDifferentNames()
    {
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\a");
        var b = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\b");
        Assert.NotEqual(a.Mutex, b.Mutex);
        Assert.NotEqual(a.Pipe, b.Pipe);
    }

    [Fact]
    public void TestRoot_DiffersFromTheUnarmedIdentity()
    {
        // The founder's running app holds the unarmed identity; an armed
        // launch must not elect under it even when everything else matches.
        var armed = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\a");
        var unarmed = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, null);
        Assert.NotEqual(armed.Mutex, unarmed.Mutex);
        Assert.NotEqual(armed.Pipe, unarmed.Pipe);
    }

    [Fact]
    public void SameTestRoot_SameNames_CaseAndTrailingSeparatorInsensitive()
    {
        // One harness reusing one root across launches (the splash-race
        // script's shape) still coordinates: same root, same identity.
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\a");
        var b = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"c:\temp\ROOTS\a\");
        Assert.Equal(a.Mutex, b.Mutex);
        Assert.Equal(a.Pipe, b.Pipe);
    }

    [Fact]
    public void MutexIsSessionLocalScoped()
    {
        var n = SingleInstanceNames.For(@"C:\X\Wintty.exe", OssEdition, null);
        Assert.StartsWith(@"Local\", n.Mutex);
        // Pipe names live under \\.\pipe\ and must not contain backslashes
        // in the user-supplied portion.
        Assert.DoesNotContain(@"\", n.Pipe);
    }
}
