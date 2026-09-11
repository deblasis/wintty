using System;
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
/// </summary>
public class TestConfigGuardTests : IDisposable
{
    private readonly string? _original;

    // The guard reads the environment on every call, so the tests set it
    // directly. Saved and restored once per test instance; nothing else in
    // this assembly reads WINTTY_TEST_CONFIG, so parallel collections
    // cannot race on it.
    public TestConfigGuardTests()
    {
        _original = Environment.GetEnvironmentVariable(TestConfigGuard.EnvVar);
    }

    public void Dispose()
    {
        if (_original is null)
            Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, null);
        else
            Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, _original);
    }

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
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, value);
        Assert.False(TestConfigGuard.IsArmed);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void ArmingSpellings_Arm_The_Guard(string value)
    {
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, value);
        Assert.True(TestConfigGuard.IsArmed);
    }

    [Fact]
    public void Unarmed_Never_Refuses_Even_Against_The_Real_Config()
    {
        // "WINTTY_TEST_CONFIG unset, behaviour unchanged" is the load-bearing
        // half of the rule: a user's install never sets the variable, so the
        // real config path must sail through untouched.
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, null);
        TestConfigGuard.AssertUnderTemp(OutsideTemp(), "resolved config path");
    }

    [Fact]
    public void Config_Under_Temp_Passes_While_Armed()
    {
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, "1");
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
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, "1");
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
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, "1");
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
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, "1");
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
        var temp = TestConfigGuard.TempAnchor.Replace('\\', '/');
        Assert.True(TestConfigGuard.IsUnderTemp(temp + "wintty-cfg-b2/wintty"));

        var upper = Temp("Wintty-Cfg-C3").ToUpperInvariant();
        Assert.True(TestConfigGuard.IsUnderTemp(upper));
    }

    // ---- M1: the anchor is the known-folder temp, not the environment --

    [Fact]
    public void The_Anchor_Ignores_A_Redirected_TEMP_And_TMP()
    {
        // The known-folder temp cannot be moved by the process
        // environment, which is the whole point of anchoring to it: a
        // harness that repoints TEMP moves Path.GetTempPath(), never this.
        using var redirect = new TempEnvRedirect("TEMP", OutsideTemp());
        using var redirect2 = new TempEnvRedirect("TMP", @"C:\");

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
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, "1");
        using var redirect = new TempEnvRedirect(name, OutsideTemp());

        var ex = Assert.Throws<InvalidOperationException>(
            () => TestConfigGuard.AssertTempEnvironmentIntact());
        Assert.Contains(name, ex.Message);
        Assert.Contains(TestConfigGuard.EnvVar, ex.Message);
    }

    [Fact]
    public void A_Drive_Root_TEMP_Is_Refused_While_Armed()
    {
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, "1");
        using var redirect = new TempEnvRedirect("TEMP", @"C:\");

        Assert.Throws<InvalidOperationException>(
            () => TestConfigGuard.AssertTempEnvironmentIntact());
    }

    [Fact]
    public void An_Intact_Temp_Environment_Passes_While_Armed()
    {
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, "1");
        using var keepTemp = new TempEnvRedirect("TEMP", TestConfigGuard.TempAnchor);
        using var keepTmp = new TempEnvRedirect("TMP", TestConfigGuard.TempAnchor);

        TestConfigGuard.AssertTempEnvironmentIntact();
    }

    [Fact]
    public void The_Temp_Environment_Is_Never_Questioned_While_Unarmed()
    {
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, null);
        using var redirect = new TempEnvRedirect("TEMP", @"C:\");

        TestConfigGuard.AssertTempEnvironmentIntact();
    }

    /// <summary>
    /// Scoped environment mutation: restores the previous value (or removes
    /// the variable) on dispose, so a failing assert cannot leak state into
    /// the next test.
    /// </summary>
    private sealed class TempEnvRedirect : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public TempEnvRedirect(string name, string value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() =>
            Environment.SetEnvironmentVariable(_name, _original);
    }

    // ---- M4: reparse points resolve before the compare -----------------

    [Fact]
    public void A_Junction_Inside_Temp_Pointing_Outside_Is_Refused()
    {
        // Junctions need no privilege, so this is the real test the brief
        // asked for: the bytes a junction carries land outside the temp
        // tree, and the guard must compare the resolved path, not the
        // textual one.
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, "1");
        var outside = Path.Combine(
            Path.GetDirectoryName(TestConfigGuard.TempAnchor)!,
            "wintty-guard-junction-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(TestConfigGuard.TempAnchor,
            "wintty-guard-junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var mklink = System.Diagnostics.Process.Start(psi)!;
        Assert.True(mklink.WaitForExit(15000), "mklink /J did not exit");
        try
        {
            Assert.True(Directory.Exists(link),
                "junction was not created: " + mklink.ExitCode);
            Assert.False(TestConfigGuard.IsUnderTemp(Path.Combine(link, "wintty")));
            Assert.Throws<InvalidOperationException>(() =>
                TestConfigGuard.AssertUnderTemp(
                    Path.Combine(link, "wintty", "config.wintty"), "config write"));
        }
        finally
        {
            Directory.Delete(link, recursive: false);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_Directory_Symlink_Inside_Temp_Pointing_Outside_Is_Refused()
    {
        // Symlink creation needs a privilege (or developer mode) the test
        // host may lack; when it does, this test says so and passes, per
        // the brief's skip rule. Junctions carry the enforced case above.
        Environment.SetEnvironmentVariable(TestConfigGuard.EnvVar, "1");
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

    // ---- L2: the xdg.zig mirror ----------------------------------------

    [Fact]
    public void The_Config_Root_Mirror_Matches_The_Xdg_Zig_Preferences()
    {
        // The four combinations that matter, mirroring xdg.zig dir():
        // a set-but-empty XDG does NOT fall to APPDATA (zig's orelse sees
        // Some("")), it falls to the home + .config default.
        using (new ScopedEnv("XDG_CONFIG_HOME", null))
        {
            using var appdata = new ScopedEnv("APPDATA", @"C:\Users\u\AppData\Roaming");
            Assert.True(string.Equals(@"C:\Users\u\AppData\Roaming",
                TestConfigGuard.ResolveConfigRoot(),
                StringComparison.OrdinalIgnoreCase));
        }
        using (new ScopedEnv("XDG_CONFIG_HOME", @"C:\scratch\xdg"))
        {
            Assert.True(string.Equals(@"C:\scratch\xdg",
                TestConfigGuard.ResolveConfigRoot(),
                StringComparison.OrdinalIgnoreCase));
        }
        using (new ScopedEnv("XDG_CONFIG_HOME", ""))
        {
            using var appdata = new ScopedEnv("APPDATA", @"C:\Users\u\AppData\Roaming");
            Assert.True(string.Equals(
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile), ".config"),
                TestConfigGuard.ResolveConfigRoot(),
                StringComparison.OrdinalIgnoreCase));
        }
        using (new ScopedEnv("XDG_CONFIG_HOME", null))
        {
            using var appdata = new ScopedEnv("APPDATA", "");
            Assert.True(string.Equals(
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile), ".config"),
                TestConfigGuard.ResolveConfigRoot(),
                StringComparison.OrdinalIgnoreCase));
        }
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

    private sealed class ScopedEnv : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public ScopedEnv(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() =>
            Environment.SetEnvironmentVariable(_name, _original);
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

/// <summary>
/// The refusing editor a --no-config run gets: writes are refused loudly,
/// reads answer as the empty file the rest of that run already serves.
/// </summary>
public class NoConfigFileEditorTests
{
    private readonly NoConfigFileEditor _editor = new();

    [Fact]
    public void FilePath_Is_Empty()
    {
        Assert.Equal(string.Empty, _editor.FilePath);
    }

    [Fact]
    public void Reads_Answer_As_An_Empty_Config()
    {
        Assert.Equal(string.Empty, _editor.ReadAll());
        Assert.Empty(_editor.GetRepeatableValues("keybind"));
    }

    [Theory]
    [InlineData("font-size", "12")]
    public void Writes_Are_Refused_Not_Silently_Dropped(string key, string value)
    {
        // A no-op write would tell a user their change persisted; the
        // refusal is the honest answer, and the debounced scheduler and
        // the startup migrator log it rather than crash on it.
        var ex = Assert.Throws<InvalidOperationException>(
            () => _editor.SetValue(key, value));
        Assert.Contains("--no-config", ex.Message);
    }

    [Fact]
    public void Every_Mutating_Method_Refuses()
    {
        Assert.Throws<InvalidOperationException>(() => _editor.RemoveValue("k"));
        Assert.Throws<InvalidOperationException>(() => _editor.WriteRaw("x = 1"));
        Assert.Throws<InvalidOperationException>(
            () => _editor.SetRepeatableValues("k", new[] { "v" }));
    }
}
