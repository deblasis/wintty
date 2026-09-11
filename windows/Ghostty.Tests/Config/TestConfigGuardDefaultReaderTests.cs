using System;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// Pins the ReadEnvironment seam's DEFAULT (re-review finding 1): the
/// shipped guard consults the real process environment and nothing else.
///
/// This class deliberately has NO injecting constructor, so whatever it
/// reads through <see cref="TestConfigGuard.ReadEnvironment"/> is the
/// production default (the injecting class restores it in Dispose, and
/// xunit finishes each test before the next starts). A default changed
/// to a constant, a snapshot, or a cache fails the live-write probe
/// below even when the equal-snapshot comparisons happen to hold.
///
/// The seam itself must stay internal: a second pin, in the wiring
/// tests, asserts the declaration from the embedded source so it cannot
/// quietly widen back to public.
/// </summary>
public class TestConfigGuardDefaultReaderTests
{
    [Fact]
    public void The_Default_Reader_Reads_The_Live_Process_Environment()
    {
        // Only names no injecting test can shadow are compared, so this
        // stays correct even if the injecting class runs concurrently:
        // its lambda falls through unknown names to the real
        // environment, which is exactly the behavior under test.
        const string probe = "WINTTY_GUARD_SEAM_PROBE";
        const string absent = "NO_SUCH_NAME_ON_PURPOSE_1093";

        Assert.Null(TestConfigGuard.ReadEnvironment(absent));
        Assert.Null(Environment.GetEnvironmentVariable(absent));

        // The decisive probe: a LIVE write through the process
        // environment is visible through the default reader. A snapshot
        // or a constant defaulted reader returns null here.
        try
        {
            Environment.SetEnvironmentVariable(probe, "round2");
            Assert.Equal("round2", TestConfigGuard.ReadEnvironment(probe));
        }
        finally
        {
            Environment.SetEnvironmentVariable(probe, null);
        }
        Assert.Null(TestConfigGuard.ReadEnvironment(probe));
    }
}
