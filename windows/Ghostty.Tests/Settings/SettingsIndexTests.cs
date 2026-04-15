using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

public class SettingsIndexTests
{
    // Every config key the settings UI currently edits must have an
    // index entry. This test is the forcing function: when a new
    // page handler calls OnValueChanged("some-key", ...), we want
    // the build to remind the author to register the key here.
    //
    // Keys come from hand-inspecting the existing Pages/*.xaml.cs
    // files as of 2026-04-14. When new config keys are wired up,
    // add them to this list AND to SettingsIndex.
    private static readonly string[] ExpectedKeys =
    {
        // General
        "auto-reload-config",
        // Appearance
        "window-theme",
        "font-family",
        "font-size",
        "background-opacity",
        "custom-shader",
        "background-style",
        "background-blur-follows-opacity",
        "background-tint-color",
        "background-tint-opacity",
        "background-luminosity-opacity",
        "background-gradient-blend",
        "background-gradient-opacity",
        "background-gradient-speed",
        "background-gradient-animation",
        "background-gradient-point",
        // Colors
        "theme",
        "foreground",
        "background",
        "cursor-color",
        "selection-background",
        // Terminal
        "scrollback-limit",
        "cursor-style",
        "cursor-style-blink",
        "mouse-hide-while-typing",
    };

    [Fact]
    public void All_contains_entry_for_every_expected_key()
    {
        var keys = SettingsIndex.All.Select(e => e.Key).ToHashSet();
        var missing = ExpectedKeys.Where(k => !keys.Contains(k)).ToList();
        Assert.True(
            missing.Count == 0,
            $"SettingsIndex.All is missing entries for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void All_has_no_duplicate_keys()
    {
        var duplicates = SettingsIndex.All
            .GroupBy(e => e.Key)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(
            duplicates.Count == 0,
            $"Duplicate keys in SettingsIndex.All: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void Every_entry_has_non_empty_label_page_section()
    {
        foreach (var e in SettingsIndex.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Label), $"Label empty for {e.Key}");
            Assert.False(string.IsNullOrWhiteSpace(e.Page),  $"Page empty for {e.Key}");
            Assert.False(string.IsNullOrWhiteSpace(e.Section),$"Section empty for {e.Key}");
        }
    }

    [Fact]
    public void Every_entry_has_at_least_one_tag()
    {
        foreach (var e in SettingsIndex.All)
        {
            Assert.NotEmpty(e.Tags);
        }
    }
}
