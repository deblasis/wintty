namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// One immutable reading of every key the default-terminal write-set
/// touches. A key that does not exist reads as empty; its appearance is
/// itself a delta.
/// </summary>
public sealed record DeftermSnapshot(
    IReadOnlyDictionary<string, IReadOnlyList<(string Name, string? Value)>> Keys);

/// <summary>
/// The machine-state canary: snapshots the exact write-set key list
/// (sourced from the committed identity constants and the same paths the
/// engine writes) and diffs two snapshots. Read-only by construction;
/// the only writes it can perform are the probe value helpers the
/// wrapper's kill-test uses, and they go through this library so no
/// other code path writes these keys outside the engine.
/// </summary>
public sealed class DeftermCanary
{
    private readonly DefaultTermRegistryPaths _paths;
    private readonly IDefaultTermIdentityView _identity;

    public DeftermCanary(
        DefaultTermRegistryPaths paths,
        IDefaultTermIdentityView identity)
    {
        _paths = paths;
        _identity = identity;
    }

    /// <summary>
    /// The library's own comparator. The kill-test pins this by
    /// reference: a substitute comparator that happens to agree is not
    /// the same proof.
    /// </summary>
    public static readonly Func<DeftermSnapshot, DeftermSnapshot, IReadOnlyList<string>> Diff =
        static (a, b) =>
        {
            var lines = new List<string>();
            foreach (var key in a.Keys.Keys.Union(b.Keys.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var left = a.Keys.TryGetValue(key, out var l) ? l : Array.Empty<(string, string?)>();
                var right = b.Keys.TryGetValue(key, out var r) ? r : Array.Empty<(string, string?)>();
                var leftMap = left.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                var rightMap = right.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
                foreach (var added in rightMap.Keys.Except(leftMap.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
                {
                    lines.Add($"{key}: +{added}");
                }

                foreach (var removed in leftMap.Keys.Except(rightMap.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
                {
                    lines.Add($"{key}: -{removed}");
                }

                foreach (var changed in leftMap.Keys.Intersect(rightMap.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (!string.Equals(leftMap[changed], rightMap[changed], StringComparison.Ordinal))
                    {
                        lines.Add($"{key}: ~{changed}");
                    }
                }
            }

            return lines;
        };

    /// <summary>Read every watched key. Never writes.</summary>
    public DeftermSnapshot Snapshot()
    {
        var keys = new Dictionary<string, IReadOnlyList<(string Name, string? Value)>>(
            StringComparer.OrdinalIgnoreCase);
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser,
            Microsoft.Win32.RegistryView.Default);
        foreach (var path in WatchedKeys())
        {
            // The whole subtree, not just the root's direct values: the
            // engine writes LocalServer32 and InprocServer32 SUBKEYS,
            // and a canary blind to subkeys is blind to exactly the
            // dangerous writes.
            CollectSubtree(baseKey, path, keys);
        }

        return new DeftermSnapshot(keys);
    }

    private static void CollectSubtree(
        Microsoft.Win32.RegistryKey baseKey,
        string path,
        Dictionary<string, IReadOnlyList<(string Name, string? Value)>> keys)
    {
        using var key = baseKey.OpenSubKey(path);
        if (key is null)
        {
            keys[path] = Array.Empty<(string, string?)>();
            return;
        }

        var values = key.GetValueNames()
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (n, key.GetValue(n) as string))
            .ToArray();
        keys[path] = values;

        foreach (var sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            CollectSubtree(baseKey, path + @"\" + sub, keys);
        }
    }

    /// <summary>
    /// The kill-test's deliberate write: one named probe value under the
    /// watched startup key, via this library, restored immediately by
    /// the caller. Nothing else may call this.
    /// </summary>
    public void WriteStartupProbe(string name, string value)
    {
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser,
            Microsoft.Win32.RegistryView.Default);
        using var key = baseKey.CreateSubKey(_paths.StartupSubkey);
        key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
        // The second write class: a value inside a subkey, where the
        // engine's dangerous writes (LocalServer32, InprocServer32,
        // ProxyStubClsid32) live.
        using var sub = key.CreateSubKey("KillProbeSub");
        sub.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
    }

    /// <summary>Restore the kill-test's probe value.</summary>
    public void DeleteStartupProbe(string name)
    {
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser,
            Microsoft.Win32.RegistryView.Default);
        using var key = baseKey.OpenSubKey(_paths.StartupSubkey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
        using var sub = key?.OpenSubKey("KillProbeSub", writable: true);
        sub?.DeleteValue(name, throwOnMissingValue: false);
    }

    private string[] WatchedKeys()
    {
        var keys = new List<string>
        {
            _paths.StartupSubkey,
            _paths.ClsidSubkeyOf(_identity.TerminalClsid),
            _paths.ClsidSubkeyOf(_identity.ConsoleClsid),
            _paths.ClsidSubkeyOf(_identity.ProxyClsid),
        };
        keys.AddRange(_identity.HandoffIids.Select(_paths.InterfaceSubkeyOf));
        return keys.ToArray();
    }
}
