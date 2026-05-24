using System;
using System.IO;
using Ghostty.Core.Config;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// Round-trip smoke test for the icon picker config-write path.
///
/// Validates that an icon written via <see cref="IConfigFileEditor.SetValue"/>
/// lands on disk in the expected `profile.&lt;id&gt;.icon = ...` shape and
/// parses back to the matching <see cref="IconSpec"/> via
/// <see cref="ProfileSourceParser.Parse"/>. Also covers
/// <see cref="IConfigFileEditor.RemoveValue"/>, which comments out the
/// line rather than deleting it; the parser then treats the profile as
/// having no icon override.
///
/// The concrete production <c>ConfigFileEditor</c> lives in the WinUI 3
/// project, which this net10.0 test assembly cannot reference. The fake
/// below mirrors the production wrapper exactly: file read, delegate to
/// the static <see cref="ConfigFileParser"/>, atomic write back.
/// </summary>
public sealed class ProfileIconPickerSmokeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly IConfigFileEditor _editor;

    public ProfileIconPickerSmokeTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "GhosttyIconPickerSmoke_" + Guid.NewGuid().ToString("N"));
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
    public void Set_BrandIcon_RoundTripsThroughConfig()
    {
        _editor.SetValue("profile.test.icon", "brand:ubuntu");

        var content = File.ReadAllText(_configPath);
        Assert.Contains("profile.test.icon = brand:ubuntu", content);

        var parsed = ProfileSourceParser.Parse(content);
        var profile = parsed.Profiles["test"];
        Assert.Equal(new IconSpec.BrandKey("ubuntu", null), profile.Icon);
    }

    [Fact]
    public void Set_Mdl2Icon_RoundTrips()
    {
        _editor.SetValue("profile.test.icon", "mdl2:E756");

        var content = File.ReadAllText(_configPath);
        Assert.Contains("profile.test.icon = mdl2:E756", content);

        var parsed = ProfileSourceParser.Parse(content);
        var profile = parsed.Profiles["test"];
        Assert.Equal(new IconSpec.Mdl2Token(0xE756), profile.Icon);
    }

    [Fact]
    public void Remove_RestoresDefaultIcon()
    {
        _editor.SetValue("profile.test.icon", "brand:ubuntu");
        _editor.RemoveValue("profile.test.icon");

        var content = File.ReadAllText(_configPath);

        // RemoveValue comments the line out rather than deleting it,
        // so the literal text "profile.test.icon" still appears, but
        // only behind a leading "# ". Assert there is no uncommented
        // occurrence by parsing through ProfileSourceParser (which
        // skips comment lines) and checking the profile has no icon.
        var parsed = ProfileSourceParser.Parse(content);
        var profile = parsed.Profiles["test"];
        Assert.Null(profile.Icon);

        // Belt-and-braces: confirm the line was commented out, not
        // re-written as a fresh assignment.
        Assert.Contains("# profile.test.icon = brand:ubuntu", content);
    }

    /// <summary>
    /// Test-only file-backed <see cref="IConfigFileEditor"/>. Mirrors the
    /// production <c>Ghostty.Services.ConfigFileEditor</c> read-modify-write
    /// loop using the same <see cref="ConfigFileParser"/> helpers. We can't
    /// reference the production class because it lives in the WinUI 3
    /// project; the logic under test is the parser + the wrapper contract,
    /// both of which this fake exercises faithfully.
    /// </summary>
    private sealed class TempFileEditor : IConfigFileEditor
    {
        public string FilePath { get; }

        public TempFileEditor(string filePath) { FilePath = filePath; }

        public string ReadAll()
            => File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;

        public void SetValue(string key, string value)
        {
            var lines = ReadLines();
            var updated = ConfigFileParser.SetValue(lines, key, value);
            Write(updated);
        }

        public void RemoveValue(string key)
        {
            var lines = ReadLines();
            var updated = ConfigFileParser.RemoveValue(lines, key);
            Write(updated);
        }

        public void WriteRaw(string content)
            => File.WriteAllText(FilePath, ConfigText.NormalizeLineEndings(content));

        public void SetRepeatableValues(string key, string[] values)
        {
            var lines = ReadLines();
            var updated = ConfigFileParser.SetRepeatableValues(lines, key, values);
            Write(updated);
        }

        private string[] ReadLines()
            => File.Exists(FilePath) ? File.ReadAllLines(FilePath) : Array.Empty<string>();

        private void Write(string[] lines)
            => File.WriteAllText(FilePath, string.Join("\n", lines) + "\n");
    }
}
