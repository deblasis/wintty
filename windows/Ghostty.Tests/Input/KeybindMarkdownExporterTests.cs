using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeybindMarkdownExporterTests
{
    private static KeybindListItem Item(string friendly, string label) =>
        new(friendly, friendly, label, default, default, KeybindSource.Default);

    private static KeybindCategory Cat(string name, params KeybindListItem[] items) =>
        new(name, items);

    [Fact]
    public void Export_RendersTitleHeadingsAndTable()
    {
        var cats = new[]
        {
            Cat("Tabs", Item("New Tab", "Ctrl+Shift+T"), Item("Close Tab", "Ctrl+Shift+W")),
            Cat("Clipboard", Item("Copy", "Ctrl+Shift+C")),
        };
        var md = KeybindMarkdownExporter.Export(cats);

        Assert.StartsWith("# Keyboard Shortcuts", md);
        Assert.Contains("## Tabs", md);
        Assert.Contains("## Clipboard", md);
        Assert.Contains("| Action | Shortcut |", md);
        Assert.Contains("| --- | --- |", md);
        Assert.Contains("| New Tab | Ctrl+Shift+T |", md);
        Assert.Contains("| Copy | Ctrl+Shift+C |", md);
    }

    [Fact]
    public void Export_EscapesPipes()
    {
        var cats = new[] { Cat("Other", Item("Weird | Action", "Ctrl+|")) };
        var md = KeybindMarkdownExporter.Export(cats);
        Assert.Contains(@"| Weird \| Action | Ctrl+\| |", md);
    }

    [Fact]
    public void Export_EmptyCatalog_IsTitleOnly()
    {
        var md = KeybindMarkdownExporter.Export(new List<KeybindCategory>());
        Assert.Equal("# Keyboard Shortcuts\n", md);
    }

    [Fact]
    public void Export_FromBuiltCatalog_Works()
    {
        var binds = new[]
        {
            new EnumeratedKeybind(
                new[] { new KeybindTrigger(0, (uint)KeyNames.OrdinalOf("key_t")!.Value, (1u << 1) | (1u << 0)) },
                "new_tab", GhosttyBindingFlags.Consumed),
        };
        var catalog = KeybindCatalog.Build(binds);
        var md = KeybindMarkdownExporter.Export(catalog);
        Assert.Contains("## Tabs", md);
        Assert.Contains("| New Tab | Ctrl+Shift+T |", md);
    }
}
