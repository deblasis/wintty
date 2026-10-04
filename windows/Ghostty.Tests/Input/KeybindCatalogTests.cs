using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeybindCatalogTests
{
    private static EnumeratedKeybind Kb(string action, uint key, uint mods)
        => new(new[] { new KeybindTrigger(0, key, mods) }, action, GhosttyBindingFlags.Consumed);

    private static List<EnumeratedKeybind> Sample() => new()
    {
        Kb("new_tab", 39, 1u | 2u),          // Tabs / Ctrl+Shift+T
        Kb("new_split:right", 16, 1u | 4u),  // Panes / equal(16) Shift+Alt -> "Alt+Shift+="? mods Shift|Alt
        Kb("copy_to_clipboard:mixed", 22, 1u | 2u), // Clipboard / key_c(22)
    };

    [Fact]
    public void Build_GroupsByCategory_SortedWithOtherLast()
    {
        var cat = KeybindCatalog.Build(Sample());
        var names = cat.Categories.Select(c => c.Name).ToList();
        // Alphabetical, but "Other" (if present) sorts last. Here: Clipboard, Panes, Tabs.
        Assert.Equal(new[] { "Clipboard", "Panes", "Tabs" }, names);
    }

    [Fact]
    public void Build_PopulatesItemFields()
    {
        var cat = KeybindCatalog.Build(Sample());
        var tabs = cat.Categories.Single(c => c.Name == "Tabs");
        var item = tabs.Items.Single();
        Assert.Equal("New Tab", item.Friendly);
        Assert.Equal("new_tab", item.RawAction);
        Assert.Equal("Ctrl+Shift+T", item.Label);
    }

    [Fact]
    public void Flatten_EmitsHeaderThenItems()
    {
        var rows = KeybindCatalog.Build(Sample()).Flatten();
        Assert.IsType<KeybindCategoryHeader>(rows[0]);
        Assert.Equal("Clipboard", ((KeybindCategoryHeader)rows[0]).Name);
        Assert.IsType<KeybindListItem>(rows[1]);
    }

    [Fact]
    public void Search_FiltersByFriendlyRawOrLabel_AndDropsEmptyGroups()
    {
        var rows = KeybindCatalog.Build(Sample()).Filter("copy");
        var headers = rows.OfType<KeybindCategoryHeader>().Select(h => h.Name).ToList();
        Assert.Equal(new[] { "Clipboard" }, headers);
        Assert.Single(rows.OfType<KeybindListItem>());
    }

    [Fact]
    public void Search_Empty_ReturnsAll()
    {
        var rows = KeybindCatalog.Build(Sample()).Filter("   ");
        Assert.Equal(3, rows.OfType<KeybindListItem>().Count());
    }

    [Fact]
    public void Build_AttachesTerminalShadowConflict()
    {
        // key_c = 22, Ctrl only -> shadow. (Action text is arbitrary here.)
        var binds = new System.Collections.Generic.List<EnumeratedKeybind>
        {
            Kb("copy_to_clipboard:mixed", 22, 1u << 1), // Ctrl+C
        };
        var cat = KeybindCatalog.Build(binds);
        var item = cat.Categories.SelectMany(c => c.Items).Single();
        Assert.Equal(ConflictKind.TerminalShadow, item.Conflict.Kind);
    }

    [Fact]
    public void Build_NoConflictForNormalChord()
    {
        var cat = KeybindCatalog.Build(Sample());
        Assert.All(cat.Categories.SelectMany(c => c.Items),
            i => Assert.Equal(ConflictKind.None, i.Conflict.Kind));
    }

    [Fact]
    public void Filter_ConflictsOnly_KeepsOnlyConflicting()
    {
        var binds = new System.Collections.Generic.List<EnumeratedKeybind>
        {
            Kb("new_tab", 39, 1u << 1),                 // Ctrl+T  (no shadow)
            Kb("copy_to_clipboard:mixed", 22, 1u << 1), // Ctrl+C  (shadow)
        };
        var rows = KeybindCatalog.Build(binds).Filter(null, conflictsOnly: true);
        var items = rows.OfType<KeybindListItem>().ToList();
        Assert.Single(items);
        Assert.Equal("copy_to_clipboard:mixed", items[0].RawAction);
    }

    [Fact]
    public void Filter_ConflictsOnly_False_ReturnsAll()
    {
        var rows = KeybindCatalog.Build(Sample()).Filter(null, conflictsOnly: false);
        Assert.Equal(3, rows.OfType<KeybindListItem>().Count());
    }

    [Fact]
    public void Build_NoDefaults_AllSourceDefault()
    {
        var cat = KeybindCatalog.Build(Sample());
        Assert.All(cat.Categories.SelectMany(c => c.Items), i => Assert.Equal(KeybindSource.Default, i.Source));
    }

    [Fact]
    public void Build_WithDefaults_ClassifiesUserVsDefault()
    {
        var current = new System.Collections.Generic.List<EnumeratedKeybind>
        {
            Kb("new_tab", 39, 1u << 1),       // Ctrl+key_t  -> matches a default below
            Kb("new_window", 22, 1u << 1),    // Ctrl+key_c  -> NOT in defaults => User
        };
        var defaults = new System.Collections.Generic.List<EnumeratedKeybind>
        {
            Kb("new_tab", 39, 1u << 1),
        };
        var cat = KeybindCatalog.Build(current, defaults);
        var items = cat.Categories.SelectMany(c => c.Items).ToList();
        Assert.Equal(KeybindSource.Default, items.Single(i => i.RawAction == "new_tab").Source);
        Assert.Equal(KeybindSource.User, items.Single(i => i.RawAction == "new_window").Source);
    }

    // The cheat sheet renders exactly what Build produces, so a user-bound
    // pin/group verb must land as a named row in its own category rather
    // than fall into "Other" with the raw action as its text.
    [Fact]
    public void Build_TabShellVerbs_RenderAsNamedRows()
    {
        var binds = new System.Collections.Generic.List<EnumeratedKeybind>
        {
            Kb("pin_tab", 39, 1u | 2u),        // Ctrl+Shift+T
            Kb("move_group:left", 20, 1u << 1) // Ctrl+key_u(20)
        };
        var cat = KeybindCatalog.Build(binds);

        var pin = cat.Categories.Single(c => c.Name == "Tabs").Items.Single(i => i.RawAction == "pin_tab");
        Assert.Equal("Pin Tab", pin.Friendly);

        var move = cat.Categories.Single(c => c.Name == "Groups").Items.Single(i => i.RawAction == "move_group:left");
        Assert.Equal("Move Group Left", move.Friendly);
    }

    // The rows-view is Clear + Add per row, which re-realizes every container
    // and puts the viewport back at the top. The page rebuilds on every config
    // reload, and every settings write is debounced through the shared
    // scheduler, so "another page changed something" was scrolling a
    // keybinding list home mid-browse. RowsMatch is what lets an unchanged
    // rebuild leave the list alone.
    [Fact]
    public void RowsMatch_IgnoresTheRebuildOfAnUnchangedCatalog()
    {
        var first = KeybindCatalog.Build(Sample()).Filter(null);
        var rebuilt = KeybindCatalog.Build(Sample()).Filter(null);

        Assert.NotSame(first, rebuilt);
        Assert.True(KeybindCatalog.RowsMatch(first, rebuilt));
    }

    [Fact]
    public void RowsMatch_NoticesARebind()
    {
        var before = KeybindCatalog.Build(Sample()).Filter(null);
        var after = KeybindCatalog.Build(new List<EnumeratedKeybind>
        {
            Kb("new_tab", 40, 1u | 2u),          // same action, different chord
            Kb("new_split:right", 16, 1u | 4u),
            Kb("copy_to_clipboard:mixed", 22, 1u | 2u),
        }).Filter(null);

        Assert.False(KeybindCatalog.RowsMatch(before, after));
    }

    [Fact]
    public void RowsMatch_NoticesARemovedRowAndADifferentFilter()
    {
        var all = KeybindCatalog.Build(Sample()).Filter(null);
        var filtered = KeybindCatalog.Build(Sample()).Filter("copy");

        Assert.False(KeybindCatalog.RowsMatch(all, filtered));
        Assert.True(KeybindCatalog.RowsMatch(filtered, filtered));
    }

    [Fact]
    public void RowsMatch_SeesTheSourceChangeOnAnOtherwiseIdenticalRow()
    {
        // Source (and conflict) travel on the row, so a keybind that became
        // the user's changes the list without changing its shape. A length-only
        // comparison would call that equal and leave the "User" tag unpainted.
        var binds = new List<EnumeratedKeybind>
        {
            Kb("new_tab", 39, 1u << 1),
            Kb("copy_to_clipboard:mixed", 22, 1u << 1),   // matches no default
        };
        var defaults = new List<EnumeratedKeybind>
        {
            Kb("new_tab", 39, 1u << 1),
        };

        var withoutDefaults = KeybindCatalog.Build(binds).Filter(null);
        var withDefaults = KeybindCatalog.Build(binds, defaults).Filter(null);

        Assert.Equal(withoutDefaults.Count, withDefaults.Count);
        Assert.False(KeybindCatalog.RowsMatch(withoutDefaults, withDefaults));
    }
}
