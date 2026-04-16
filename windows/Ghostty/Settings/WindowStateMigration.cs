using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Ghostty.Core.Config;

namespace Ghostty.Settings;

/// <summary>
/// One-shot migration from the pre-split <c>ui-settings.json</c>
/// into the real ghostty config file. Reads the three fields the
/// migration covers (vertical-tabs, command-palette-group-commands,
/// command-palette-background), delegates shape-neutral append
/// computation to <see cref="LegacyUiSettingsMigrator"/>, writes
/// appended keys via <see cref="IConfigFileEditor"/>, and finally
/// rewrites the shim as a <see cref="WindowState"/>-only payload
/// so the next run skips the legacy branch.
///
/// Idempotent: after the first successful run the legacy file is
/// either renamed or rewritten without the migrated keys, so
/// subsequent runs produce no appends.
/// </summary>
internal static class WindowStateMigration
{
    public static void TryRun(IConfigService configService, IConfigFileEditor editor)
    {
        try
        {
            var legacyPath = WindowState.LegacyFilePath;
            var newPath = WindowState.FilePath;

            // If the new path already exists, the migration has already
            // run (we only rename the legacy file on successful run).
            // Bail out early to keep subsequent launches cheap.
            if (!File.Exists(legacyPath) || File.Exists(newPath)) return;

            var json = File.ReadAllText(legacyPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool GetBool(string name)
                => root.TryGetProperty(name, out var p) &&
                   p.ValueKind == JsonValueKind.True;

            string? GetString(string name)
                => root.TryGetProperty(name, out var p) &&
                   p.ValueKind == JsonValueKind.String
                   ? p.GetString() : null;

            var legacy = new LegacyUiSettingsPayload(
                VerticalTabs: GetBool("VerticalTabs"),
                CommandPaletteGroupCommands: GetBool("CommandPaletteGroupCommands"),
                CommandPaletteBackground: GetString("CommandPaletteBackground"));

            // Read the user's config file directly so we can tell whether
            // each migrated key is already set. IConfigService exposes
            // IsConfiguredInFile, but its backing cache is only alive
            // during ReadFlags() scope and is nulled in the finally, so
            // calling it from here (outside a reload) returns false for
            // every key. Scanning three substrings out of a small file
            // once at startup is cheaper than threading a live snapshot
            // through the service, and scopes the workaround to this
            // one-shot migrator; fixing the latent cache bug is a
            // separate concern tracked by the typed-accessor pattern.
            var existing = ReadExistingKeysFromConfig(
                configService.ConfigFilePath,
                [
                    "vertical-tabs",
                    "command-palette-group-commands",
                    "command-palette-background",
                ]);

            var appends = LegacyUiSettingsMigrator.ComputeAppends(legacy, existing);
            if (appends.Count > 0)
            {
                configService.SuppressWatcher(true);
                try
                {
                    foreach (var (key, value) in appends)
                        editor.SetValue(key, value);
                }
                finally
                {
                    configService.SuppressWatcher(false);
                }
                configService.Reload();
            }

            // Persist the remaining (placement-only) fields under the
            // new filename. This is what lets the "already migrated"
            // early-return fire on future launches. Deletion of the
            // legacy file happens after the new file is written so a
            // crash mid-migration leaves the user recoverable.
            var placement = new WindowState
            {
                WindowX = root.TryGetProperty("WindowX", out var x) && x.ValueKind == JsonValueKind.Number
                    ? x.GetInt32() : null,
                WindowY = root.TryGetProperty("WindowY", out var y) && y.ValueKind == JsonValueKind.Number
                    ? y.GetInt32() : null,
                WindowWidth = root.TryGetProperty("WindowWidth", out var w) && w.ValueKind == JsonValueKind.Number
                    ? w.GetInt32() : null,
                WindowHeight = root.TryGetProperty("WindowHeight", out var h) && h.ValueKind == JsonValueKind.Number
                    ? h.GetInt32() : null,
                WindowMaximized = root.TryGetProperty("WindowMaximized", out var m) && m.ValueKind == JsonValueKind.True,
            };
            placement.Save();

            // Best-effort delete; swallow I/O errors so a locked file
            // does not block startup. WindowState.Load already prefers
            // the new path when both exist.
            try { File.Delete(legacyPath); }
            catch (Exception ex) { Debug.WriteLine($"WindowStateMigration: legacy delete failed: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WindowStateMigration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Minimal ini scan over the user's config file, restricted to
    /// the given interesting keys. Matches ghostty's own parser
    /// semantics for the things we actually care about: skip blank
    /// and '#'-prefixed lines, split on the first '=', case-insensitive
    /// key compare, and treat any non-empty value as "set".
    /// </summary>
    private static HashSet<string> ReadExistingKeysFromConfig(
        string configPath, string[] interesting)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            return set;

        var lookup = new HashSet<string>(interesting, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                var key = trimmed[..eq].Trim();
                if (!lookup.Contains(key)) continue;
                var value = trimmed[(eq + 1)..].Trim();
                if (value.Length == 0) continue;
                set.Add(key);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WindowStateMigration: config scan failed: {ex.Message}");
        }
        return set;
    }
}
