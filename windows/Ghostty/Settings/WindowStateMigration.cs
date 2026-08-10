using System;
using System.IO;
using System.Text.Json;
using Ghostty.Core.Config;
using Ghostty.Logging;
using Ghostty.Services;
using Microsoft.Extensions.Logging;

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
    // Takes the concrete service rather than IConfigService: the only
    // member it needs beyond the interface is IsConfiguredInFile, and
    // IConfigService is public, so widening it would break the tier
    // overlays' hand-written stubs for the sake of one internal caller.
    public static void TryRun(ConfigService configService, IConfigFileEditor editor)
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

            // Never overwrite a key the user has already set in their
            // config; the legacy JSON only fills in what is missing.
            var appends = LegacyUiSettingsMigrator.ComputeAppends(
                legacy, configService.IsConfiguredInFile);
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
            catch (Exception ex) { StaticLoggers.WindowStateMigration.LogLegacyDeleteFailed(ex); }
        }
        catch (Exception ex)
        {
            StaticLoggers.WindowStateMigration.LogMigrationFailed(ex);
        }
    }
}

internal static partial class WindowStateMigrationLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.WindowState.MigrationFailed,
                   Level = LogLevel.Warning,
                   Message = "WindowStateMigration failed")]
    internal static partial void LogMigrationFailed(
        this ILogger logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Logging.LogEvents.WindowState.MigrationLegacyDeleteFailed,
                   Level = LogLevel.Warning,
                   Message = "WindowStateMigration: legacy delete failed")]
    internal static partial void LogLegacyDeleteFailed(
        this ILogger logger, System.Exception ex);
}
