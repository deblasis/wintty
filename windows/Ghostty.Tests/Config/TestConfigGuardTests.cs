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
        var path = Path.GetTempPath();
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
        Assert.True(TestConfigGuard.IsUnderTemp(Path.GetTempPath()));
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
        Assert.Contains(Path.GetTempPath(), ex.Message);
    }

    [Fact]
    public void Prefix_Sibling_Of_Temp_Is_Not_Under_Temp()
    {
        // C:\...\Temp-evil shares every character of the temp prefix; the
        // separator requirement is what refuses it.
        var temp = Path.GetTempPath().TrimEnd(
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
        var temp = Path.GetTempPath().Replace('\\', '/');
        Assert.True(TestConfigGuard.IsUnderTemp(temp + "wintty-cfg-b2/wintty"));

        var upper = Temp("Wintty-Cfg-C3").ToUpperInvariant();
        Assert.True(TestConfigGuard.IsUnderTemp(upper));
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
