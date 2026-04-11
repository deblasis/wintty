using System.Collections.Generic;

namespace Ghostty.Core.Config;

/// <summary>
/// Binding entry for display in UI. Action is the ghostty action
/// name (e.g. "new_tab"), KeyCombo is the human-readable chord
/// (e.g. "Ctrl+Shift+T"), Source indicates default vs user override.
/// </summary>
public readonly record struct BindingEntry(
    string Action,
    string KeyCombo,
    string Source);

public interface IKeyBindingsProvider
{
    /// <summary>All current bindings for UI display.</summary>
    IReadOnlyList<BindingEntry> All { get; }

    /// <summary>Get the key combo for a given action, or null.</summary>
    string? GetBinding(string action);

    /// <summary>Search bindings by action name or key combo substring.</summary>
    IReadOnlyList<BindingEntry> Search(string query);
}
