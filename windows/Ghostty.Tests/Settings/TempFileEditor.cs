using System;
using System.IO;
using Ghostty.Core.Config;

namespace Ghostty.Tests.Settings;

/// <summary>
/// Test-only file-backed <see cref="IConfigFileEditor"/>. Mirrors the
/// production <c>Ghostty.Services.ConfigFileEditor</c> read-modify-write
/// loop using the same <see cref="ConfigFileParser"/> helpers. We can't
/// reference the production class because it lives in the WinUI 3
/// project; the logic under test is the parser + the wrapper contract,
/// both of which this fake exercises faithfully.
///
/// Shared across smoke tests that need to round-trip a config key
/// through SetValue / RemoveValue and the parser.
/// </summary>
internal sealed class TempFileEditor : IConfigFileEditor
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

    public string[] GetRepeatableValues(string key)
        => ConfigFileParser.GetRepeatableValues(ReadLines(), key);

    private string[] ReadLines()
        => File.Exists(FilePath) ? File.ReadAllLines(FilePath) : Array.Empty<string>();

    private void Write(string[] lines)
        => File.WriteAllText(FilePath, string.Join("\n", lines) + "\n");
}
