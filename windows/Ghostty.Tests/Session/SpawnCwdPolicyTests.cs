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
}
