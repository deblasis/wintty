using System.Collections.Generic;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

public class LegacyUiSettingsMigratorTests
{
    [Fact]
    public void Emits_pairs_for_all_three_keys_when_absent_from_config()
    {
        var legacy = new LegacyUiSettingsPayload(
            VerticalTabs: true,
            CommandPaletteGroupCommands: true,
            CommandPaletteBackground: "mica");
        var existingKeys = new HashSet<string>();

        var pairs = LegacyUiSettingsMigrator.ComputeAppends(legacy, existingKeys);

        Assert.Contains(("vertical-tabs", "true"), pairs);
        Assert.Contains(("command-palette-group-commands", "true"), pairs);
        Assert.Contains(("command-palette-background", "mica"), pairs);
    }

    [Fact]
    public void Skips_keys_already_present_in_config()
    {
        var legacy = new LegacyUiSettingsPayload(true, true, "opaque");
        var existingKeys = new HashSet<string> { "vertical-tabs" };

        var pairs = LegacyUiSettingsMigrator.ComputeAppends(legacy, existingKeys);

        Assert.DoesNotContain(pairs, p => p.Key == "vertical-tabs");
        Assert.Contains(("command-palette-group-commands", "true"), pairs);
        Assert.Contains(("command-palette-background", "opaque"), pairs);
    }

    [Fact]
    public void Omits_defaults_to_avoid_bloating_the_config()
    {
        // All three at defaults (false, false, "acrylic") -> no appends.
        var legacy = new LegacyUiSettingsPayload(false, false, "acrylic");
        var pairs = LegacyUiSettingsMigrator.ComputeAppends(legacy, new HashSet<string>());
        Assert.Empty(pairs);
    }

    [Fact]
    public void Handles_null_background_gracefully()
    {
        var legacy = new LegacyUiSettingsPayload(true, false, null);
        var pairs = LegacyUiSettingsMigrator.ComputeAppends(legacy, new HashSet<string>());
        Assert.Contains(("vertical-tabs", "true"), pairs);
        Assert.DoesNotContain(pairs, p => p.Key == "command-palette-background");
    }

    [Fact]
    public void Normalizes_background_case()
    {
        var legacy = new LegacyUiSettingsPayload(false, false, "MICA");
        var pairs = LegacyUiSettingsMigrator.ComputeAppends(legacy, new HashSet<string>());
        Assert.Contains(("command-palette-background", "mica"), pairs);
    }
}
