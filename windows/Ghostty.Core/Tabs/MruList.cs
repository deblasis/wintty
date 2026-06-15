using System.Collections.Generic;

namespace Ghostty.Core.Tabs;

/// <summary>
/// A small ordered set, most-recently-touched first. Used by
/// <see cref="TabManager"/> to drive the Ctrl+Tab MRU cycle and to order
/// the tab overview. Reference equality (the default for the
/// <see cref="TabModel"/> reference type it is instantiated with).
/// </summary>
internal sealed class MruList<T>
{
    private readonly List<T> _items = new();

    /// <summary>Most-recently-touched first.</summary>
    public IReadOnlyList<T> Order => _items;

    /// <summary>Move <paramref name="item"/> to the front, inserting it if absent.</summary>
    public void Touch(T item)
    {
        _items.Remove(item);
        _items.Insert(0, item);
    }

    public void Remove(T item) => _items.Remove(item);
}
