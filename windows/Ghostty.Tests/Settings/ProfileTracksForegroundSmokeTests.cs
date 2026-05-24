using System;
using System.IO;
using Ghostty.Core.Config;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// Round-trip smoke test for the <c>tab-icon-tracks-foreground</c>
/// per-profile config key.
///
/// Validates that the value written via <see cref="IConfigFileEditor.SetValue"/>
/// lands on disk in the expected `profile.&lt;id&gt;.tab-icon-tracks-foreground = ...`
/// shape and parses back to the matching <c>TabIconTracksForeground</c>
/// flag on <see cref="ProfileDef"/>. Also covers
/// <see cref="IConfigFileEditor.RemoveValue"/>, which comments out the
/// line rather than deleting it; the parser then treats the profile as
/// having no override and the default (true) is restored.
/// </summary>
public sealed class ProfileTracksForegroundSmokeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly IConfigFileEditor _editor;

    public ProfileTracksForegroundSmokeTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "GhosttyTracksForegroundSmoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config");

        File.WriteAllText(_configPath,
            "profile.test.name = Test\n" +
            "profile.test.command = pwsh.exe\n");

        _editor = new TempFileEditor(_configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Set_False_Persists()
    {
        _editor.SetValue("profile.test.tab-icon-tracks-foreground", "false");

        var content = File.ReadAllText(_configPath);
        Assert.Contains("profile.test.tab-icon-tracks-foreground = false", content);

        var parsed = ProfileSourceParser.Parse(content);
        var profile = parsed.Profiles["test"];
        Assert.False(profile.TabIconTracksForeground);
    }

    [Fact]
    public void Set_True_Persists()
    {
        _editor.SetValue("profile.test.tab-icon-tracks-foreground", "true");

        var content = File.ReadAllText(_configPath);
        Assert.Contains("profile.test.tab-icon-tracks-foreground = true", content);

        var parsed = ProfileSourceParser.Parse(content);
        var profile = parsed.Profiles["test"];
        Assert.True(profile.TabIconTracksForeground);
    }

    [Fact]
    public void Remove_RestoresDefaultTrue()
    {
        _editor.SetValue("profile.test.tab-icon-tracks-foreground", "false");
        _editor.RemoveValue("profile.test.tab-icon-tracks-foreground");

        var content = File.ReadAllText(_configPath);

        // RemoveValue comments the line out rather than deleting it,
        // matching the behavior verified by the icon-picker smoke test.
        Assert.Contains("# profile.test.tab-icon-tracks-foreground = false", content);

        // The parser skips comment lines, so the profile has no
        // override and the default (true) is restored.
        var parsed = ProfileSourceParser.Parse(content);
        var profile = parsed.Profiles["test"];
        Assert.True(profile.TabIconTracksForeground);
    }
}
