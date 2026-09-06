using Ghostty.Core.Session;
using Xunit;

namespace Ghostty.Tests.Session;

/// <summary>
/// The gate a shell-reported directory passes before it can become a spawn
/// directory. A reported cwd is bytes off the pty, and Duplicate Tab, Reopen
/// Closed Tab and session restore all hand it to CreateProcess -- which opens
/// an SMB connection to whatever server a UNC path names, and authenticates
/// to it. This layer decides who may receive the user's credentials, and had
/// no tests of its own.
///
/// It mirrors <c>posix_path.pathHost</c> on the native side. The two
/// disagreeing is how a path comes to be refused by one layer and spawned by
/// the other, so the cases below are deliberately the same ones.
/// </summary>
public class SpawnCwdPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingReported_IsNotADirectoryToSpawnIn(string? cwd)
        => Assert.False(SpawnCwdPolicy.MaySpawnAt(cwd));

    [Theory]
    [InlineData(@"C:\Users\alex")]
    [InlineData("C:/Users/alex")]
    [InlineData(@"Z:\work")]
    // The long-path spelling of a real directory.
    [InlineData(@"\\?\C:\Users\alex")]
    public void ALocalDirectory_MaySpawn(string cwd)
        => Assert.True(SpawnCwdPolicy.MaySpawnAt(cwd));

    [Theory]
    // Served by the local WSL service rather than by SMB over the wire.
    [InlineData(@"\\wsl.localhost\Ubuntu-24.04\home\alex")]
    [InlineData(@"\\wsl$\Ubuntu-24.04\home\alex")]
    [InlineData(@"\\WSL.LOCALHOST\Ubuntu\home\alex")]
    [InlineData(@"\\localhost\c$\Users\alex")]
    [InlineData(@"\\127.0.0.1\share")]
    public void AShareThatNeverLeavesTheMachine_MaySpawn(string cwd)
        => Assert.True(SpawnCwdPolicy.MaySpawnAt(cwd));

    [Theory]
    [InlineData(@"\\evil.example.com\share")]
    [InlineData(@"\\evil.example.com\share\deep\dir")]
    // Win32 folds forward slashes before resolving, so the same reach spelled
    // the other way has to be refused too.
    [InlineData("//evil.example.com/share")]
    // The second spelling of the same reach, through the extended prefix.
    [InlineData(@"\\?\UNC\evil.example.com\share")]
    [InlineData(@"\\?\unc\evil.example.com\share")]
    public void AShareOnAnotherMachine_MayNotSpawn(string cwd)
        => Assert.False(SpawnCwdPolicy.MaySpawnAt(cwd));

    /// <summary>
    /// The device namespace shares its shape with the extended-length prefix
    /// but not its permitted continuations. <c>\\?\C:\dir</c> is a real
    /// directory; <c>\\.\C:</c> is a handle to the volume object. Treating
    /// the two prefixes as one let a device path borrow the drive-letter
    /// escape and reach CreateProcess.
    /// </summary>
    [Theory]
    [InlineData(@"\\.\C:\dir")]
    [InlineData(@"\\.\C:")]
    [InlineData(@"\\.\COM1")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\\.\UNC\evil.example.com\share")]
    [InlineData(@"\\?\GLOBALROOT\Device\X")]
    public void ADevicePath_MayNotSpawn(string cwd)
        => Assert.False(SpawnCwdPolicy.MaySpawnAt(cwd));

    /// <summary>
    /// Windows does not normalize separators inside the extended-length
    /// prefix, so <c>//?/UNC/host/share</c> is not an extended path at all --
    /// it is a plain UNC path naming the host <c>?</c>, and it is refused as
    /// one rather than parsed as a prefix.
    /// </summary>
    [Fact]
    public void AForwardSlashPrefix_IsAHostNamedQuestionMark_AndIsRefused()
        => Assert.False(SpawnCwdPolicy.MaySpawnAt("//?/UNC/evil.example.com/share"));

    [Theory]
    [InlineData(@"\\")]
    [InlineData(@"\\\share")]
    public void APathNamingNoHost_MayNotSpawn(string cwd)
        => Assert.False(SpawnCwdPolicy.MaySpawnAt(cwd));

    /// <summary>
    /// The spawn funnel demands plain text as well as an acceptable host.
    ///
    /// The terminal core refuses a path carrying control characters at the
    /// source now, but this path reaches further back than the core can: a
    /// restored session was written by whatever build recorded it, and
    /// builds before that guard had no such check. Testing only the host
    /// here meant a directory refused for the clipboard, for Explorer, for
    /// the label and for the tooltip was still handed to CreateProcess.
    /// </summary>
    [Theory]
    [InlineData("C:\\Users\\alex\nevil")]
    [InlineData("C:\\Users\\alex\u202Eevil")]
    [InlineData("C:\\Users\\alex\u2028evil")]
    [InlineData("C:\\Users\\alex\u007Fevil")]
    public void ADirectoryThatIsNotPlainText_IsNotSpawnedInto(string cwd)
    {
        // The host itself is fine -- it is the bytes that are not, which is
        // why checking the host alone let these through.
        Assert.True(SpawnCwdPolicy.MaySpawnAt(cwd));

        var resolved = SessionProfileResolver.ResolveLeaf(
            registry: null,
            new LeafDto
            {
                ProfileId = null,
                Fallback = new LeafCommand { ResolvedCommand = "pwsh.exe", DisplayName = "p" },
                Cwd = cwd,
            });

        Assert.NotNull(resolved);
        // The profile's own directory stands; the reported one is dropped.
        Assert.Null(resolved!.WorkingDirectory);
    }

    /// <summary>The same funnel still honours a directory that IS plain.</summary>
    [Fact]
    public void APlainLocalDirectory_IsSpawnedInto()
    {
        var resolved = SessionProfileResolver.ResolveLeaf(
            registry: null,
            new LeafDto
            {
                ProfileId = null,
                Fallback = new LeafCommand { ResolvedCommand = "pwsh.exe", DisplayName = "p" },
                Cwd = @"C:\Users\alex\src",
            });

        Assert.Equal(@"C:\Users\alex\src", resolved!.WorkingDirectory);
    }
}
