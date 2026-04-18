using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Tracks which update versions the user has explicitly skipped.
/// Persisted as JSON to a user-local file (caller supplies path;
/// shell passes %LOCALAPPDATA%\Ghostty\update-skips.json).
/// Uses exact-match set membership (ordinal string equality): skipping
/// 1.4.2 silences exactly 1.4.2; later versions surfaced by a subsequent
/// check are evaluated independently.
/// </summary>
public sealed class UpdateSkipList
{
    private readonly string _path;
    private readonly HashSet<string> _skipped;

    public UpdateSkipList(string path)
    {
        _path = path;
        _skipped = Load(path);
    }

    /// <summary>
    /// Raised after <see cref="Skip"/> actually adds a new version.
    /// The pill ViewModel listens so it can re-project and hide itself
    /// when the user dismisses the currently-announced update.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Add a version to the skip list and persist.</summary>
    public void Skip(string version)
    {
        if (_skipped.Add(version))
        {
            Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// True iff <paramref name="version"/> is in the skip list. Exact
    /// string match (ordinal): skipping 1.4.2 does not silence 1.4.3 or
    /// later — the next check surfaces newer versions normally.
    /// </summary>
    public bool IsSkipped(string version) => _skipped.Contains(version);

    private static HashSet<string> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<string>>(json);
            return list is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(list, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] skip-list load failed: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            var json = JsonSerializer.Serialize(new List<string>(_skipped));
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] skip-list save failed: {ex.Message}");
        }
    }
}
