using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Ghostty.Core;

/// <summary>
/// AOT-safe replacement for WinUI 3's reflection-based
/// <c>{Binding PropertyName}</c> in DataTemplates. Subscribes to
/// <see cref="INotifyPropertyChanged"/> on a data item and invokes
/// an update action when watched properties change.
///
/// WinUI 3's <c>{Binding}</c> resolves property paths via runtime
/// reflection, which NativeAOT trims. This helper avoids reflection
/// entirely: the caller provides a typed update delegate that reads
/// the model directly.
/// </summary>
internal sealed class AotBinding : IDisposable
{
    private readonly object _item;
    private readonly Action<object> _update;
    private readonly HashSet<string>? _watchedProperties;
    private bool _disposed;

    /// <summary>The data item this binding is attached to.</summary>
    public object Item => _item;

    private AotBinding(object item, Action<object> update, HashSet<string>? watchedProperties)
    {
        _item = item;
        _update = update;
        _watchedProperties = watchedProperties;
    }

    /// <summary>
    /// Create a binding that watches specific property names.
    /// The update action runs immediately and again whenever a
    /// watched property fires <see cref="INotifyPropertyChanged"/>.
    /// </summary>
    /// <param name="item">The data item (typically a model object).</param>
    /// <param name="update">
    /// Callback that reads the model and writes to the view.
    /// Called on the thread that raises PropertyChanged.
    /// </param>
    /// <param name="properties">
    /// Property names to watch. Null or empty means watch all.
    /// </param>
    public static AotBinding Create(
        object item,
        Action<object> update,
        params string[] properties)
    {
        var watched = properties.Length > 0
            ? new HashSet<string>(properties, StringComparer.Ordinal)
            : null;

        var binding = new AotBinding(item, update, watched);

        if (item is INotifyPropertyChanged inpc)
            inpc.PropertyChanged += binding.OnPropertyChanged;

        // Run the update immediately so the view reflects the
        // current model state without waiting for a change.
        update(item);

        return binding;
    }

    /// <summary>
    /// Detach the INPC subscription. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_item is INotifyPropertyChanged inpc)
            inpc.PropertyChanged -= OnPropertyChanged;
    }

    internal void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        // null/empty PropertyName means "all properties changed"
        if (_watchedProperties is not null
            && !string.IsNullOrEmpty(e.PropertyName)
            && !_watchedProperties.Contains(e.PropertyName))
        {
            return;
        }

        _update(_item);
    }
}
