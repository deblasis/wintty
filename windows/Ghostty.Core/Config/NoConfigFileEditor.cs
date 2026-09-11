using System;

namespace Ghostty.Core.Config;

/// <summary>
/// The editor a <c>--no-config</c> run gets instead of a real
/// <c>ConfigFileEditor</c>. The flag exists to ignore the config file, so
/// the process that flew it must not write that file either: constructing
/// the real editor over the resolved path used to leave every Settings-UI
/// write, profile hide toggle and opacity drag landing in the real config
/// on a run that was told to ignore it.
///
/// Reads answer as an empty file, matching what the rest of a
/// <c>--no-config</c> run already serves: <c>ConfigSourcePath</c> is null
/// and <c>SeedConfigIfEmpty</c> returns early. Writes throw, because the
/// two silent alternatives are both worse: a no-op write reports success to
/// a user whose change then vanishes, and a real write is the taint this
/// class exists to stop. The debounced scheduler and the startup migrator
/// catch per-write exceptions and log them, so a refusal surfaces as a
/// warning line rather than a crash.
/// </summary>
public sealed class NoConfigFileEditor : IConfigFileEditor
{
    /// <summary>No file backs this editor, so there is no path to name.</summary>
    public string FilePath => string.Empty;

    /// <summary>Nothing is in force, so nothing is on disk to read.</summary>
    public string ReadAll() => string.Empty;

    public void SetValue(string key, string value) => Refuse();

    public void RemoveValue(string key) => Refuse();

    public void WriteRaw(string content) => Refuse();

    public void SetRepeatableValues(string key, string[] values) => Refuse();

    public string[] GetRepeatableValues(string key) => Array.Empty<string>();

    private static void Refuse() => throw new InvalidOperationException(
        "this process runs with --no-config, so no config file may be " +
        "written; the change was not persisted");
}
