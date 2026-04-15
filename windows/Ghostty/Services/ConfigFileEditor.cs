using System;
using System.IO;
using Ghostty.Core.Config;

namespace Ghostty.Services;

/// <summary>
/// File I/O wrapper around <see cref="ConfigFileParser"/>.
/// Reads/writes the config file on disk with atomic writes
/// (write-to-temp, then rename) to prevent partial reads.
/// </summary>
internal sealed class ConfigFileEditor : IConfigFileEditor
{
    public string FilePath { get; }

    public ConfigFileEditor(string filePath)
    {
        FilePath = filePath;
    }

    public string ReadAll()
    {
        return File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
    }

    public void SetValue(string key, string value)
    {
        var lines = ReadLines();
        var updated = ConfigFileParser.SetValue(lines, key, value);
        WriteAtomic(updated);
    }

    public void RemoveValue(string key)
    {
        var lines = ReadLines();
        var updated = ConfigFileParser.RemoveValue(lines, key);
        WriteAtomic(updated);
    }

    public void WriteRaw(string content)
    {
        // See ConfigText.NormalizeLineEndings for why this is required.
        WriteAtomic(ConfigText.NormalizeLineEndings(content));
    }

    public void SetRepeatableValues(string key, string[] values)
    {
        var lines = ReadLines();
        var updated = ConfigFileParser.SetRepeatableValues(lines, key, values);
        WriteAtomic(updated);
    }

    private string[] ReadLines()
    {
        return File.Exists(FilePath) ? File.ReadAllLines(FilePath) : Array.Empty<string>();
    }

    private void WriteAtomic(string[] lines)
    {
        // Use \n (not Environment.NewLine) so the config file stays
        // Unix-style, matching what ghostty's own writer produces and
        // avoiding issues if the file is shared with WSL or synced
        // to a non-Windows machine.
        WriteAtomic(string.Join("\n", lines) + "\n");
    }

    private void WriteAtomic(string content)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (dir != null) Directory.CreateDirectory(dir);

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
