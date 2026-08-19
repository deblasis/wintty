using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Config;

namespace Ghostty.Services;

internal sealed partial class KeyBindingsProvider : IKeyBindingsProvider, IDisposable
{
    private readonly IConfigService _configService;
    private List<BindingEntry> _bindings = new();

    public IReadOnlyList<BindingEntry> All => _bindings;

    public KeyBindingsProvider(IConfigService configService)
    {
        _configService = configService;
        _configService.ConfigChanged += OnConfigChanged;
        Refresh();
    }

    public string? GetBinding(string action)
    {
        foreach (var b in _bindings)
        {
            if (b.Action == action) return b.KeyCombo;
        }
        return null;
    }

    public IReadOnlyList<BindingEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _bindings;
        return _bindings
            .Where(b => b.Action.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || b.KeyCombo.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public void Dispose()
    {
        _configService.ConfigChanged -= OnConfigChanged;
    }

    private void OnConfigChanged(IConfigService _) => Refresh();

    private void Refresh()
    {
        // Only the Windows-only residual chords have C# labels now;
        // libghostty owns matching (and the chord text) for every
        // standard action. When ghostty_config_trigger P/Invoke is
        // wired this will query libghostty for each known action and
        // the standard chords reappear here.
        var entries = new List<BindingEntry>();
        foreach (var b in Input.KeyBindings.WindowsOnly.All)
        {
            var label = Input.KeyBindings.WindowsOnly.Label(b.Action);
            if (label != null)
            {
                entries.Add(new BindingEntry(
                    b.Action.ToString(),
                    label,
                    "default"));
            }
        }
        _bindings = entries;
    }
}
