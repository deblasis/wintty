namespace Ghostty.Core.Config;

/// <summary>
/// Reads and writes the ghostty config file on disk.
/// Supports surgical "sniper edit" (modify one key, preserve
/// everything else) and raw full-file read/write.
/// </summary>
public interface IConfigFileEditor
{
    /// <summary>Path to the config file being edited.</summary>
    string FilePath { get; }

    /// <summary>Read the entire config file as a string.</summary>
    string ReadAll();

    /// <summary>
    /// Set a config value. If the key already exists (uncommented),
    /// replaces the last occurrence. Otherwise appends at the end.
    /// </summary>
    void SetValue(string key, string value);

    /// <summary>
    /// Comment out all uncommented occurrences of a key (prefix with "# ").
    /// </summary>
    void RemoveValue(string key);

    /// <summary>Write raw text content to the config file atomically.</summary>
    void WriteRaw(string content);
}
