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
}
