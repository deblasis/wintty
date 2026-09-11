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
    public void SamePathEditionRootMarkerAndSession_ProducesStableNames()
    {
        var a = SingleInstanceNames.For(@"C:\Program Files\Wintty\Wintty.exe", OssEdition, null, false, 1);
        var b = SingleInstanceNames.For(@"C:\Program Files\Wintty\Wintty.exe", OssEdition, null, false, 1);
        Assert.Equal(a.Mutex, b.Mutex);
        Assert.Equal(a.Pipe, b.Pipe);
    }

    [Fact]
    public void PathIsCaseAndSeparatorInsensitive()
    {
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty\Wintty.exe", OssEdition, null, false, 1);
        var b = SingleInstanceNames.For(@"c:/apps/wintty/wintty.exe", OssEdition, null, false, 1);
        Assert.Equal(a.Mutex, b.Mutex);
        Assert.Equal(a.Pipe, b.Pipe);
    }

    [Fact]
    public void DifferentPaths_SameEdition_ProduceDifferentNames()
    {
        // A dev tree beside an installed app is two installs of one edition;
        // the dev launch must not hand itself to the installed primary.
        var a = SingleInstanceNames.For(@"C:\A\Wintty.exe", OssEdition, null, false, 1);
        var b = SingleInstanceNames.For(@"C:\B\Wintty.exe", OssEdition, null, false, 1);
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
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, null, false, 1);
        var b = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", ProEdition, null, false, 1);
        Assert.NotEqual(a.Mutex, b.Mutex);
        Assert.NotEqual(a.Pipe, b.Pipe);
    }

    /// <summary>
    /// The test isolation story (#1094): every harness arms the marker over
    /// its own random config root, and two different roots must be two
    /// identities or a test launch forwards into another harness's app.
    /// </summary>
    [Fact]
    public void DifferentRoots_SamePathAndEdition_ProduceDifferentNames()
    {
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\a", true, 1);
        var b = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\b", true, 1);
        Assert.NotEqual(a.Mutex, b.Mutex);
        Assert.NotEqual(a.Pipe, b.Pipe);
    }

    /// <summary>
    /// The marker is part of the material even when the root is too, so an
    /// armed launch and an unarmed launch never share an election however
    /// their config roots are spelled (#1094 review L1: the one isolation
    /// direction that must hold absolutely).
    /// </summary>
    [Fact]
    public void ArmedAndUnarmed_WithTheSameRoot_ProduceDifferentNames()
    {
        var armed = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\a", true, 1);
        var unarmed = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\a", false, 1);
        Assert.NotEqual(armed.Mutex, unarmed.Mutex);
        Assert.NotEqual(armed.Pipe, unarmed.Pipe);
    }

    /// <summary>
    /// #1094 review L1: the config root is identity material even WITHOUT
    /// the marker. A harness that points XDG_CONFIG_HOME at a scratch root
    /// but forgets to arm the test marker must not forward into the real
    /// instance: any launch whose config root differs is its own process.
    /// </summary>
    [Fact]
    public void UnarmedDifferentRoots_ProduceDifferentNames()
    {
        var real = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, null, false, 1);
        var misconfigured = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\x", false, 1);
        Assert.NotEqual(real.Mutex, misconfigured.Mutex);
        Assert.NotEqual(real.Pipe, misconfigured.Pipe);
    }

    [Fact]
    public void SameRoot_SameNames_CaseAndTrailingSeparatorInsensitive()
    {
        // One harness reusing one root across launches (the splash-race
        // script's shape) still coordinates: same root, same identity.
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\a", true, 1);
        var b = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"c:\temp\ROOTS\a\", true, 1);
        Assert.Equal(a.Mutex, b.Mutex);
        Assert.Equal(a.Pipe, b.Pipe);
    }

    /// <summary>
    /// #1094 review M1: named pipes live in one per-machine namespace while
    /// the Local\ mutex is per terminal-services session, so the session id
    /// must reach BOTH names or the same user's second session elects its
    /// own primary and then forwards its launches into the first session's
    /// pipe. Pinning the pipe explicitly, not just the mutex.
    /// </summary>
    [Fact]
    public void DifferentSessions_PipeAndMutexDiffer_SameSessionStable()
    {
        var a = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, null, false, 1);
        var b = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, null, false, 2);
        Assert.NotEqual(a.Mutex, b.Mutex);
        Assert.NotEqual(a.Pipe, b.Pipe);

        var a2 = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, null, false, 1);
        Assert.Equal(a.Pipe, a2.Pipe);
    }

    /// <summary>
    /// #1094 review L3: a device-prefixed root spelling must hash like the
    /// plain one, or the same root in two spellings elects two primaries.
    /// </summary>
    [Fact]
    public void DevicePrefixedRoot_HashesLikeThePlainRoot()
    {
        var plain = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"C:\Temp\roots\a", true, 1);
        var prefixed = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"\\?\C:\Temp\roots\a", true, 1);
        Assert.Equal(plain.Mutex, prefixed.Mutex);
        Assert.Equal(plain.Pipe, prefixed.Pipe);
    }

    /// <summary>
    /// #1094 review L3: a relative root spelling resolves against the
    /// working directory, so two launches that spell the same relative root
    /// while sitting in the same directory share an election, and the
    /// false-share direction (same spelling, different directories) does
    /// not. Pinned without mutating the process's own directory: the
    /// relative spelling must equal the pre-resolved absolute one.
    /// </summary>
    [Fact]
    public void RelativeRoot_ResolvesAgainstTheWorkingDirectory()
    {
        var absolute = Path.Combine(Environment.CurrentDirectory, "roots", "a");
        var relative = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, @"roots\a", true, 1);
        var spelled = SingleInstanceNames.For(@"C:\Apps\Wintty.exe", OssEdition, absolute, true, 1);
        Assert.Equal(spelled.Mutex, relative.Mutex);
        Assert.Equal(spelled.Pipe, relative.Pipe);
    }

    [Fact]
    public void MutexIsSessionLocalScoped()
    {
        var n = SingleInstanceNames.For(@"C:\X\Wintty.exe", OssEdition, null, false, 1);
        Assert.StartsWith(@"Local\", n.Mutex);
        // Pipe names live under \\.\pipe\ and must not contain backslashes
        // in the user-supplied portion.
        Assert.DoesNotContain(@"\", n.Pipe);
    }
}
