using System;
using System.IO;
using System.Text.RegularExpressions;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.TestHost;

/// <summary>
/// The test HOST is armed (audit M1): a [ModuleInitializer] in every test
/// project points this process at a per-host random temp config root and
/// arms WINTTY_TEST_CONFIG, so an in-process test that reaches for a
/// default config path (through a Ghostty.Core helper, or after someone
/// adds the obvious project reference) cannot silently read or write the
/// real per-user config. No launch is involved, which is exactly why the
/// source scan could never cover this class of entry: the enforcement has
/// to live in the host process itself.
/// </summary>
public class TestHostArmingTests
{
    [Fact]
    public void The_Test_Host_Runs_Armed_On_A_Random_Temp_Root()
    {
        // The guard's own arm state, read fresh (not cached), because the
        // initializer ran long before any test.
        Assert.True(TestConfigGuard.IsArmed,
            "the test host must arm WINTTY_TEST_CONFIG; a test that " +
            "genuinely needs a clean environment takes the documented " +
            "WINTTY_TEST_HOST_UNARMED escape hatch");

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Assert.False(string.IsNullOrEmpty(xdg));
        Assert.True(TestConfigGuard.IsUnderTemp(xdg),
            $"XDG_CONFIG_HOME '{xdg}' must resolve under the known-folder " +
            $"temp anchor '{TestConfigGuard.TempAnchor}'");

        // Random-named leaf, same discipline as every harness: a fixed
        // name would collide across hosts and runs.
        var leaf = new DirectoryInfo(xdg).Name;
        Assert.Matches(new Regex("^wintty-testhost-[0-9a-f]{16,}$"), leaf);
    }

    [Fact]
    public void The_Host_Root_Exists_And_Stayed_Inside_The_Anchor()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Assert.False(string.IsNullOrEmpty(xdg));
        Assert.True(Directory.Exists(xdg),
            "the armed host root must exist for the whole host lifetime");
    }
}
