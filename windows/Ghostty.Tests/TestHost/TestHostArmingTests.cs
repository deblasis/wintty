using System;
using System.IO;
using System.Text.RegularExpressions;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.TestHost;

/// <summary>
/// Collections that read the LIVE process environment serialize here, so
/// no parallel collection can be mid-mutation while these observe. (After
/// review finding M1 the guard tests no longer touch the live environment
/// at all - they inject <see cref="TestConfigGuard.ReadEnvironment"/> - so
/// today nothing mutates it after the initializer; the collection is the
/// belt that keeps that true for every future env-reading test.)
/// </summary>
[CollectionDefinition("EnvironmentSerial", DisableParallelization = true)]
public class EnvironmentSerialCollection { }

/// <summary>
/// The test HOST is armed (audit M1): a [ModuleInitializer] in every test
/// project points this process at a per-host random temp config root and
/// arms WINTTY_TEST_CONFIG, so an in-process test that reaches for a
/// default config path (through a Ghostty.Core helper, or after someone
/// adds the obvious project reference) cannot silently read or write the
/// real per-user config. No launch is involved, which is exactly why the
/// source scan could never cover this class of entry: the enforcement has
/// to live in the host process itself.
///
/// The SNAPSHOT asserts read what the initializer decided at host start
/// (TestHostArming.Armed / .Root) and cannot be flipped by anything that
/// happens mid-run; the LIVE asserts read the real environment and sit in
/// the serialized collection (review finding M1).
/// </summary>
[Collection("EnvironmentSerial")]
public class TestHostArmingTests
{
    [Fact]
    public void The_Host_Was_Armed_At_Startup()
    {
        // This failing message IS the loud unarmed warning: neither
        // Console.WriteLine nor Console.Error survives dotnet test's
        // normal verbosity (both proven swallowed), so the summary line
        // a human reads is this assert's text. It names the hatch, so a
        // deliberate clean-env run explains itself right here.
        Assert.True(Ghostty.Testing.TestHostArming.Armed,
            "test host UNARMED via WINTTY_TEST_HOST_UNARMED" +
            (Environment.GetEnvironmentVariable(
                "WINTTY_TEST_HOST_UNARMED") == "1"
                ? " (the documented hatch is active; nothing isolates this host)"
                : " (the initializer did not arm; this is NOT the hatch)") +
            ". Every test in this host runs against the REAL config " +
            "unless the run is stopped.");

        var root = Ghostty.Testing.TestHostArming.RootForTests;
        Assert.False(string.IsNullOrEmpty(root));
        Assert.True(TestConfigGuard.IsUnderTemp(root),
            $"the host root '{root}' must resolve under the known-folder " +
            $"temp anchor '{TestConfigGuard.TempAnchor}'");

        // Random-named leaf, same discipline as every harness: a fixed
        // name would collide across hosts and runs.
        var leaf = new DirectoryInfo(root).Name;
        Assert.Matches(new Regex("^wintty-testhost-[0-9a-f]{16,}$"), leaf);
    }

    [Fact]
    public void The_Live_Environment_Matches_The_Snapshot()
    {
        // From the serialized collection only: the live environment is
        // read here, and nothing parallel may be mutating it.
        if (Ghostty.Testing.TestHostArming.Armed)
        {
            Assert.Equal("1",
                Environment.GetEnvironmentVariable(TestConfigGuard.EnvVar));
            Assert.Equal(
                Ghostty.Testing.TestHostArming.RootForTests,
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
            Assert.True(Directory.Exists(
                Ghostty.Testing.TestHostArming.RootForTests!),
                "the armed host root must exist for the whole host lifetime");
        }
        else
        {
            // The documented hatch: the sentinel assert above already
            // failed loudly in that case, so reaching here means a test
            // run deliberately took the hatch.
            Assert.Null(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        }
    }
}
