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
/// legacy JSON afterwards. Idempotent: rerunning against the same
/// <paramref name="isConfigured"/> and <paramref name="legacy"/>
/// always yields the same list. Default values are intentionally
/// omitted so the migration does not bloat the user's config with
/// values that already match the built-in defaults.
/// </summary>
public static class LegacyUiSettingsMigrator
{
    /// <param name="isConfigured">
    /// Answers whether the user's config already sets a given key; those
    /// keys are left alone. Taken as a predicate rather than a set so the
    /// key names live here only, and a caller cannot pass a set that has
    /// drifted from the keys this method actually asks about.
    /// </param>
    public static IReadOnlyList<(string Key, string Value)> ComputeAppends(
        LegacyUiSettingsPayload legacy,
        Func<string, bool> isConfigured)
    {
        var result = new List<(string, string)>();

        if (legacy.VerticalTabs && !isConfigured("vertical-tabs"))
            result.Add(("vertical-tabs", "true"));

        if (legacy.CommandPaletteGroupCommands &&
            !isConfigured("command-palette-group-commands"))
            result.Add(("command-palette-group-commands", "true"));

        if (!string.IsNullOrWhiteSpace(legacy.CommandPaletteBackground) &&
            !legacy.CommandPaletteBackground.Trim().Equals(
                "acrylic", StringComparison.OrdinalIgnoreCase) &&
            !isConfigured("command-palette-background"))
        {
            result.Add(("command-palette-background",
                legacy.CommandPaletteBackground.Trim().ToLowerInvariant()));
        }

        return result;
    }
}
