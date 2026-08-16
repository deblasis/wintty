using Ghostty.Core;
using Ghostty.Core.Version;
using Xunit;

namespace Ghostty.Tests;

/// <summary>
/// The AUMID is baked in per tier and variant, so these assert the shape
/// of whatever the build produced rather than one fixed string. Only the
/// OSS default is pinned to an exact value, guarded on the edition, so a
/// wintty-release build overriding it does not have to patch this file.
/// StateDirName is guarded the same way, since the shared tiering patch
/// sets Edition per tier and is expected to make the state dir per-flavour
/// too.
/// </summary>
public sealed class AppIdentityTests
{
    [Fact]
    public void AumId_ComesFromTheBuild()
    {
        Assert.Equal(BuildInfo.AumId, AppIdentity.AumId);
    }

    [Fact]
    public void AumId_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppIdentity.AumId));
    }

    [Fact]
    public void AumId_HasNoWhitespace()
    {
        // SetCurrentProcessExplicitAppUserModelID rejects whitespace, and a
        // build-time override is the kind of place a stray space arrives.
        Assert.DoesNotContain(AppIdentity.AumId, char.IsWhiteSpace);
    }

    [Fact]
    public void AumId_FitsTheShellLimit()
    {
        // Windows caps an AppUserModelID at 128 characters.
        Assert.True(AppIdentity.AumId.Length <= 128, $"AUMID is {AppIdentity.AumId.Length} chars: {AppIdentity.AumId}");
    }

    [Fact]
    public void AumId_DefaultsToTheUnsuffixedIdInAnOssBuild()
    {
        // Edition is a compile-time constant, so this folds away rather than
        // branching. Written as a guarded assert, not an early return, since
        // the return would be unreachable code in an OSS build (CS0162).
        if (BuildInfo.Edition == Edition.Oss)
        {
            Assert.Equal("com.deblasis.wintty", AppIdentity.AumId);
        }
    }

    [Fact]
    public void StateDirName_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(AppIdentity.StateDirName));
    }

    [Fact]
    public void StateDirName_SurvivesWin32PathNormalization()
    {
        // A state directory name has to survive Win32 path normalisation:
        // invalid characters are rejected outright, and a trailing space or
        // dot is silently stripped, which would collide two flavours.
        Assert.Matches(@"^[A-Za-z0-9._-]*[A-Za-z0-9_-]$", AppIdentity.StateDirName);
    }

    [Fact]
    public void StateDirName_DefaultsToWinttyInAnOssBuild()
    {
        if (BuildInfo.Edition == Edition.Oss)
        {
            Assert.Equal("Wintty", AppIdentity.StateDirName);
        }
    }
}
