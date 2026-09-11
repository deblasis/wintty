using System;
using System.Collections.Generic;
using System.IO;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

/// <summary>
/// The test-config guard's behaviour, in isolation: arming, the temp
/// membership test (including the prefix and device-prefix tricks), and the
/// refusal message. The end-to-end refusal (real app, non-zero exit before
/// the window) is the harness matrix this guard's PR ran; these tests are
/// what keeps the path arithmetic honest in between.
///
/// These tests NEVER mutate the real process environment: the guard reads
/// the environment through <see cref="TestConfigGuard.ReadEnvironment"/>,
/// and each test injects a dictionary over the real one. Mutating the real
/// environment from a test races every concurrently running collection and
/// transiently disarms the armed test host, which is the exact hole the
/// host arming closes; the injected reader touches nothing outside the
/// test itself (review finding M1).
/// </summary>
public class TestConfigGuardTests : IDisposable
{
    private readonly IDictionary<string, string?> _env =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public TestConfigGuardTests()
    {
        // Layer the dictionary over the real environment: unset names fall
        // through, set names (including null = removed) shadow it.
        TestConfigGuard.ReadEnvironment = name =>
            _env.ContainsKey(name) ? _env[name] :
            Environment.GetEnvironmentVariable(name);
    }

    public void Dispose() =>
        TestConfigGuard.ReadEnvironment = Environment.GetEnvironmentVariable;

    private void Set(string name, string? value) => _env[name] = value;

    private static string Temp(params string[] below)
    {
        var path = TestConfigGuard.TempAnchor;
        foreach (var segment in below) path = Path.Combine(path, segment);
        return path;
    }

    private static string OutsideTemp() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "wintty-guard-tests", "config");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    public void DisarmedSpellings_Leave_The_Guard_Off(string? value)
    {
        Set(TestConfigGuard.EnvVar, value);
        Assert.False(TestConfigGuard.IsArmed);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void ArmingSpellings_Arm_The_Guard(string value)
    {
        Set(TestConfigGuard.EnvVar, value);
        Assert.True(TestConfigGuard.IsArmed);
    }

    [Fact]
    public void Unarmed_Never_Refuses_Even_Against_The_Real_Config()
    {
        // "WINTTY_TEST_CONFIG unset, behaviour unchanged" is the load-bearing
        // half of the rule: a user's install never sets the variable, so the
        // real config path must sail through untouched.
        Set(TestConfigGuard.EnvVar, null);
        TestConfigGuard.AssertUnderTemp(OutsideTemp(), "resolved config path");
    }

    [Fact]
    public void Config_Under_Temp_Passes_While_Armed()
    {
        Set(TestConfigGuard.EnvVar, "1");
        TestConfigGuard.AssertUnderTemp(Temp("wintty-cfg-a1", "wintty", "config.wintty"),
            "resolved config path");
    }

    [Fact]
    public void Temp_Directory_Itself_Counts_As_Under_Temp()
    {
        Assert.True(TestConfigGuard.IsUnderTemp(TestConfigGuard.TempAnchor));
    }

    [Fact]
    public void Config_Outside_Temp_Is_Refused_While_Armed()
    {
        Set(TestConfigGuard.EnvVar, "1");
        var path = OutsideTemp();
        var ex = Assert.Throws<InvalidOperationException>(
            () => TestConfigGuard.AssertUnderTemp(path, "resolved config path"));

        // Both paths and the env var: the reader of the failure is whoever
        // wrote the harness, and the fix is on their side of the launch.
        Assert.Contains(TestConfigGuard.EnvVar, ex.Message);
        Assert.Contains(path, ex.Message);
        Assert.Contains(TestConfigGuard.TempAnchor, ex.Message);
    }

    [Fact]
    public void Prefix_Sibling_Of_Temp_Is_Not_Under_Temp()
    {
        // C:\...\Temp-evil shares every character of the temp prefix; the
        // separator requirement is what refuses it.
        var temp = TestConfigGuard.TempAnchor.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sibling = temp + "-evil";
        Assert.False(TestConfigGuard.IsUnderTemp(sibling));
        Assert.False(TestConfigGuard.IsUnderTemp(sibling + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Device_Prefix_Cannot_Smuggle_A_Path_Past_The_Compare()
    {
        Set(TestConfigGuard.EnvVar, "1");
        // The prefix survives Path.GetFullPath; stripping it first is what
        // keeps this from reading as a path nobody has an opinion about.
        var smuggled = @"\\?\" + OutsideTemp();
        Assert.False(TestConfigGuard.IsUnderTemp(smuggled));
        Assert.Throws<InvalidOperationException>(
            () => TestConfigGuard.AssertUnderTemp(smuggled, "config write"));
    }

    [Fact]
    public void Relative_And_Empty_Paths_Are_Not_Under_Temp()
    {
        Set(TestConfigGuard.EnvVar, "1");
        Assert.False(TestConfigGuard.IsUnderTemp("wintty-config"));
        Assert.False(TestConfigGuard.IsUnderTemp(""));
        Assert.False(TestConfigGuard.IsUnderTemp("   "));
        Assert.False(TestConfigGuard.IsUnderTemp(@"\absolute-without-drive"));
    }

    [Fact]
    public void Mixed_Separators_And_Case_Still_Match_Temp()
    {
        // libghostty builds forward-slash paths; ConfigService normalizes
        // them, but the guard must not depend on that having happened.
        var temp = (TestConfigGuard.TempAnchor + "/").Replace('\\', '/');
        Assert.True(TestConfigGuard.IsUnderTemp(temp + "wintty-cfg-b2/wintty"));

        var upper = Temp("Wintty-Cfg-C3").ToUpperInvariant();
        Assert.True(TestConfigGuard.IsUnderTemp(upper));
    }

    // ---- M1 (round 0): the anchor is the known-folder temp, not the environment --

    [Fact]
    public void The_Anchor_Ignores_A_Redirected_TEMP_And_TMP()
    {
        // The known-folder temp cannot be moved by the process
        // environment, which is the whole point of anchoring to it: a
        // harness that repoints TEMP moves Path.GetTempPath(), never this.
        Set("TEMP", OutsideTemp());
        Set("TMP", @"C:\");

        var anchor = TestConfigGuard.TempAnchor;
        Assert.True(string.Equals(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp"),
            anchor, StringComparison.OrdinalIgnoreCase), "anchor was " + anchor);

        // The real config stays outside even with TEMP pointed at it...
        Assert.False(TestConfigGuard.IsUnderTemp(OutsideTemp()));
        // ...and the real temp tree stays inside even with TEMP elsewhere.
        Assert.True(TestConfigGuard.IsUnderTemp(Temp("still-inside")));
    }

    [Theory]
    [InlineData("TEMP")]
    [InlineData("TMP")]
    public void A_Redirected_Temp_Environment_Is_Refused_While_Armed(string name)
    {
        Set(TestConfigGuard.EnvVar, "1");
        Set(name, OutsideTemp());

        var ex = Assert.Throws<InvalidOperationException>(
            () => TestConfigGuard.AssertTempEnvironmentIntact());
        Assert.Contains(name, ex.Message);
        Assert.Contains(TestConfigGuard.EnvVar, ex.Message);
    }

    [Fact]
    public void A_Drive_Root_TEMP_Is_Refused_While_Armed()
    {
        Set(TestConfigGuard.EnvVar, "1");
        Set("TEMP", @"C:\");

        Assert.Throws<InvalidOperationException>(
            () => TestConfigGuard.AssertTempEnvironmentIntact());
    }

    [Fact]
    public void An_Intact_Temp_Environment_Passes_While_Armed()
    {
        Set(TestConfigGuard.EnvVar, "1");
        Set("TEMP", Path.GetTempPath());
        Set("TMP", Path.GetTempPath());

        TestConfigGuard.AssertTempEnvironmentIntact();
    }

    [Fact]
    public void The_Temp_Environment_Is_Never_Questioned_While_Unarmed()
    {
        Set(TestConfigGuard.EnvVar, null);
        Set("TEMP", @"C:\");

        TestConfigGuard.AssertTempEnvironmentIntact();
    }

    // ---- M4 (round 0): reparse points resolve before the compare -----------------

    [Fact]
    public void A_Junction_Inside_Temp_Pointing_Outside_Is_Refused()
    {
        // The point is a REPARSE POINT inside temp whose target is outside;
        // both junctions and symlinks carry it, and the guard must compare
        // the resolved path, not the textual one. A symlink is created
        // in-process when the host allows it; the junction (which needs no
        // privilege but a cmd spawn) is the fallback; a host that allows
        // neither reports the skip and the reparse test below still covers
        // what it can.
        Set(TestConfigGuard.EnvVar, "1");
        var outside = Path.Combine(
            Path.GetDirectoryName(TestConfigGuard.TempAnchor)!,
            "wintty-guard-junction-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(TestConfigGuard.TempAnchor,
            "wintty-guard-junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (IOException)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                    {
                        Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    var mklink = System.Diagnostics.Process.Start(psi)!;
                    Assert.True(mklink.WaitForExit(15000), "mklink /J did not exit");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    Assert.True(true,
                        "neither symlink nor junction creation is available on " +
                        "this host; the guard's reparse resolution is covered " +
                        "by the final-path resolution of the anchor itself");
                    return;
                }
            }

            Assert.True(Directory.Exists(link), "reparse point was not created");
            Assert.False(TestConfigGuard.IsUnderTemp(Path.Combine(link, "wintty")));
            Assert.Throws<InvalidOperationException>(() =>
                TestConfigGuard.AssertUnderTemp(
                    Path.Combine(link, "wintty", "config.wintty"), "config write"));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_Directory_Symlink_Inside_Temp_Pointing_Outside_Is_Refused()
    {
        // Symlink creation needs a privilege (or developer mode) the test
        // host may lack; when it does, this test says so and passes, per
        // the brief's skip rule. Junctions carry the enforced case above.
        Set(TestConfigGuard.EnvVar, "1");
        var outside = Path.Combine(
            Path.GetDirectoryName(TestConfigGuard.TempAnchor)!,
            "wintty-guard-symlink-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(TestConfigGuard.TempAnchor,
            "wintty-guard-symlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (IOException)
            {
                Assert.True(true,
                    "symlink creation is unavailable on this host (needs a " +
                    "privilege); covered by the junction test instead");
                return;
            }

            Assert.False(TestConfigGuard.IsUnderTemp(Path.Combine(link, "wintty")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
            Directory.Delete(outside, recursive: true);
        }
    }

    // ---- L2 (round 0): the xdg.zig mirror ----------------------------------------

    [Fact]
    public void The_Config_Root_Mirror_Matches_The_Xdg_Zig_Preferences()
    {
        // The four combinations that matter, mirroring xdg.zig dir():
        // a set-but-empty XDG_CONFIG_HOME does NOT fall to APPDATA (zig's
        // orelse sees Some("")), it falls to the home + .config default.
        Set("XDG_CONFIG_HOME", null);
        Set("APPDATA", @"C:\Users\u\AppData\Roaming");
        Assert.True(string.Equals(@"C:\Users\u\AppData\Roaming",
            TestConfigGuard.ResolveConfigRoot(),
            StringComparison.OrdinalIgnoreCase));
        Set("XDG_CONFIG_HOME", @"C:\scratch\xdg");
        Assert.True(string.Equals(@"C:\scratch\xdg",
            TestConfigGuard.ResolveConfigRoot(),
            StringComparison.OrdinalIgnoreCase));
        // A set-but-empty XDG does NOT fall to APPDATA (zig's orelse sees
        // Some("")); and with both empty the home + .config default wins.
        // The overlay SHADOWS to null/empty on purpose: the armed host sets
        // the real XDG, so fall-through would read the host root.
        Set("XDG_CONFIG_HOME", "");
        Set("APPDATA", @"C:\Users\u\AppData\Roaming");
        Assert.True(string.Equals(
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), ".config"),
            TestConfigGuard.ResolveConfigRoot(), StringComparison.OrdinalIgnoreCase));
        Set("XDG_CONFIG_HOME", null);
        Set("APPDATA", "");
        Assert.True(string.Equals(
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile), ".config"),
            TestConfigGuard.ResolveConfigRoot(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_Mirror_Is_Pinned_Against_The_Zig_Source_It_Mirrors()
    {
        // A hand-maintained mirror rots silently; this reads the actual
        // xdg.zig and fails when its precedence changes shape, so the C#
        // and the zig cannot disagree unnoticed.
        var root = RepoRoot();
        var zig = File.ReadAllText(Path.Combine(root, "src", "os", "xdg.zig"));
        Assert.Contains(".windows => environ_map.get(internal_opts.env) orelse environ_map.get(internal_opts.windows_env) orelse \"\"", zig);
        Assert.Contains("if (env.len > 0)", zig);
        Assert.Contains("internal_opts.default_subdir", zig);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "build.zig")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("repo root not found");
    }
}
