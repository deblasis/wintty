using System;
using System.Collections.Generic;
using System.Linq;

namespace Ghostty.Core.Input;

/// <summary>One displayed keybind row.</summary>
public sealed record KeybindListItem(string Friendly, string RawAction, string Label, Interop.GhosttyBindingFlags Flags, KeybindConflict Conflict, KeybindSource Source);

/// <summary>A category header row in the flattened list.</summary>
public sealed record KeybindCategoryHeader(string Name);

/// <summary>A category with its ordered items.</summary>
public sealed record KeybindCategory(string Name, IReadOnlyList<KeybindListItem> Items);

/// <summary>
/// Turns an enumerated keybind list into ordered, category-grouped, searchable
/// rows for the read-only editor. Pure; the WinUI page renders the result.
/// </summary>
public sealed class KeybindCatalog
{
    public IReadOnlyList<KeybindCategory> Categories { get; }

    private KeybindCatalog(IReadOnlyList<KeybindCategory> categories) => Categories = categories;

    public static KeybindCatalog Build(
        IReadOnlyList<EnumeratedKeybind> binds,
        IReadOnlyList<EnumeratedKeybind>? defaults = null)
    {
        var defaultKeys = defaults is null
            ? null
            : KeybindSourceClassifier.BuildDefaultKeys(defaults);

        var byCategory = new Dictionary<string, List<KeybindListItem>>(StringComparer.Ordinal);
        foreach (var kb in binds)
        {
            var desc = KeybindActionCatalog.Describe(kb.Action);
            var source = defaultKeys is null
                ? KeybindSource.Default
                : KeybindSourceClassifier.Classify(kb, defaultKeys);
            var item = new KeybindListItem(
                desc.Friendly, kb.Action, TriggerLabeler.Describe(kb), kb.Flags,
                KeybindConflictAnalyzer.Analyze(kb), source);
            if (!byCategory.TryGetValue(desc.Category, out var list))
                byCategory[desc.Category] = list = new List<KeybindListItem>();
            list.Add(item);
        }

        var categories = byCategory
            .Select(kv => new KeybindCategory(
                kv.Key,
                kv.Value.OrderBy(i => i.Friendly, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(c => c.Name == "Other" ? 1 : 0)            // "Other" last
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new KeybindCatalog(categories);
    }

    /// <summary>Header + item rows for a flat ListView (all categories).</summary>
    public IReadOnlyList<object> Flatten() => FlattenFrom(Categories);

    /// <summary>Filtered header + item rows; empty groups dropped. Optional conflicts-only.</summary>
    public IReadOnlyList<object> Filter(string? query, bool conflictsOnly = false)
    {
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        if (!hasQuery && !conflictsOnly) return Flatten();

        var q = query?.Trim() ?? string.Empty;
        var filtered = Categories
            .Select(c => new KeybindCategory(
                c.Name,
                c.Items.Where(i =>
                    (!hasQuery || Matches(i, q)) &&
                    (!conflictsOnly || i.Conflict.HasConflict)).ToList()))
            .Where(c => c.Items.Count > 0)
            .ToList();
        return FlattenFrom(filtered);
    }

    private static bool Matches(KeybindListItem i, string q)
        => i.Friendly.Contains(q, StringComparison.OrdinalIgnoreCase)
        || i.RawAction.Contains(q, StringComparison.OrdinalIgnoreCase)
        || i.Label.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<object> FlattenFrom(IReadOnlyList<KeybindCategory> categories)
    {
        var rows = new List<object>();
        foreach (var c in categories)
        {
            rows.Add(new KeybindCategoryHeader(c.Name));
            rows.AddRange(c.Items);
        }

        return rows;
    }
}
