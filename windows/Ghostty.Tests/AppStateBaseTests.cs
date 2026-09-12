using System;
using System.Collections.Generic;
using Ghostty.Core;
using Xunit;

namespace Ghostty.Tests;

/// <summary>
/// The state-base override's behaviour (#854 item 9): unset it changes
/// nothing, set it replaces the known-folder root, and the blank and
/// padded spellings a hand-written `set` produces are normalized rather
/// than combined into the path. The wiring half -- that every state path
/// actually goes through the helper -- lives in
/// <c>Wiring.AppStateBaseWiringTests</c>.
///
/// These tests NEVER mutate the real process environment: the roots are
/// read through <see cref="AppStateBase.ReadEnvironment"/>, and each
/// test injects a dictionary over the real one, the same shape
/// <c>Config.TestConfigGuardTests</c> uses. Mutating the real
/// environment from a test races every concurrently running collection
/// and transiently redirects every other test's state paths.
/// </summary>
public class AppStateBaseTests : IDisposable
{
    private readonly IDictionary<string, string?> _env =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public AppStateBaseTests()
    {
        // Layer the dictionary over the real environment: unset names
        // fall through, set names (including null = removed) shadow it.
        AppStateBase.ReadEnvironment = name =>
            _env.ContainsKey(name) ? _env[name] :
            Environment.GetEnvironmentVariable(name);
    }

    public void Dispose() =>
        AppStateBase.ReadEnvironment = Environment.GetEnvironmentVariable;

    private void Set(string? value) => _env[AppStateBase.EnvVar] = value;

    private static string RealLocalRoot => Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData);

    private static string RealRoamingRoot => Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void UnsetOrBlankSpellings_Keep_The_Real_Known_Folders(string? value)
    {
        // A user's install never sets the variable: unset and every
        // blank spelling must be byte-for-byte the pre-override value,
        // not the blank combined into a relative path.
        Set(value);

        Assert.Null(AppStateBase.OverrideRoot);
        Assert.Equal(RealLocalRoot, AppStateBase.LocalRoot);
        Assert.Equal(RealRoamingRoot, AppStateBase.RoamingRoot);
        Assert.Equal(RealLocalRoot, AppStateBase.ApplyOverride(RealLocalRoot));
    }

    [Fact]
    public void ASetValue_Replaces_Both_Roots()
    {
        // Both halves of the state tree move together: the override
        // trades the local/roaming split for one tree to point at, so a
        // harness redirects every state file with one variable.
        Set(@"C:\harness\state");

        Assert.Equal(@"C:\harness\state", AppStateBase.OverrideRoot);
        Assert.Equal(@"C:\harness\state", AppStateBase.LocalRoot);
        Assert.Equal(@"C:\harness\state", AppStateBase.RoamingRoot);
        Assert.Equal(@"C:\harness\state", AppStateBase.ApplyOverride(RealLocalRoot));
    }

    [Theory]
    [InlineData(" C:\\harness\\state ", "C:\\harness\\state")]
    [InlineData("\tC:\\harness\\state\n", "C:\\harness\\state")]
    public void APaddedValue_Is_Trimmed_Not_Combined(string raw, string trimmed)
    {
        // A stray space or trailing quote from a hand-written `set` must
        // not silently become part of the path.
        _env[AppStateBase.EnvVar] = raw;

        Assert.Equal(trimmed, AppStateBase.OverrideRoot);
        Assert.Equal(trimmed, AppStateBase.LocalRoot);
    }

    [Fact]
    public void The_Env_Var_Is_Named_For_What_It_Overrides()
    {
        // The name is load-bearing twice over: harnesses spell it, and
        // the app-run tooling that will attribute per-process state
        // trees reads it. A rename here breaks both with no compile-time
        // signal, so the spelling is pinned.
        Assert.Equal("WINTTY_STATE_BASE", AppStateBase.EnvVar);
    }
}
