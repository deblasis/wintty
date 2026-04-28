using System;
using System.Collections.Generic;

namespace Ghostty.Core.Config;

/// <summary>
/// Shape of legacy ui-settings.json (no window placement).
/// </summary>
public sealed record LegacyUiSettingsPayload(
    bool VerticalTabs,
    bool CommandPaletteGroupCommands,
    string? CommandPaletteBackground);

/// <summary>
/// One-shot migrator from legacy ui-settings.json to real ghostty
/// config key/value appends. Produces no side effects -- the caller
/// writes pairs via <see cref="IConfigFileEditor"/> and prunes the
/// legacy JSON afterwards. Idempotent: rerunning with the same
/// <paramref name="existingKeys"/> and <paramref name="legacy"/>
/// always yields the same list. Default values are intentionally
/// omitted so the migration does not bloat the user's config with
/// values that already match the built-in defaults.
/// </summary>
public static class LegacyUiSettingsMigrator
{
    public static IReadOnlyList<(string Key, string Value)> ComputeAppends(
        LegacyUiSettingsPayload legacy,
        ISet<string> existingKeys)
    {
        var result = new List<(string, string)>();

        if (legacy.VerticalTabs && !existingKeys.Contains("vertical-tabs"))
            result.Add(("vertical-tabs", "true"));

        if (legacy.CommandPaletteGroupCommands &&
            !existingKeys.Contains("command-palette-group-commands"))
            result.Add(("command-palette-group-commands", "true"));

        if (!string.IsNullOrWhiteSpace(legacy.CommandPaletteBackground) &&
            !legacy.CommandPaletteBackground.Trim().Equals(
                "acrylic", StringComparison.OrdinalIgnoreCase) &&
            !existingKeys.Contains("command-palette-background"))
        {
            result.Add(("command-palette-background",
                legacy.CommandPaletteBackground.Trim().ToLowerInvariant()));
        }

        return result;
    }
}
