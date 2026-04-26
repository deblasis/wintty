using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Pure-logic backing model for the new-tab split-button dropdown.
/// Subscribes to <see cref="IProfileRegistry.ProfilesChanged"/> and
/// rebuilds <see cref="Rows"/> in registry order on each fire.
/// Lives in <c>Ghostty.Core</c> so the cross-platform test project
/// can drive it without a WinUI host. The WinUI-side VM at
/// <c>Ghostty/Tabs/NewTabSplitButtonViewModel.cs</c> wraps this and
/// adds <c>BitmapImage</c> resolution per row.
/// </summary>
public sealed class NewTabFlyoutController : IDisposable
{
    public readonly record struct Row(string Id, string Name, bool IsDefault);

    private readonly IProfileRegistry _registry;
    private readonly ObservableCollection<Row> _rows = new();
    private bool _disposed;

    public NewTabFlyoutController(IProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _registry.ProfilesChanged += OnProfilesChanged;
        Rebuild();
    }

    public IReadOnlyList<Row> Rows => _rows;

    private void OnProfilesChanged(IProfileRegistry _) => Rebuild();

    private void Rebuild()
    {
        if (_disposed) return;
        _rows.Clear();
        foreach (var p in _registry.Profiles)
            _rows.Add(new Row(p.Id, p.Name, p.IsDefault));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registry.ProfilesChanged -= OnProfilesChanged;
    }
}
