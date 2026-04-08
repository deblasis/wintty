using System.Collections.Generic;
using Windows.System;

namespace Ghostty.Input;

/// <summary>
/// One binding: a modifier set + a key + the <see cref="PaneAction"/>
/// the chord invokes.
/// </summary>
internal readonly record struct KeyBinding(
    VirtualKeyModifiers Modifiers,
    VirtualKey Key,
    PaneAction Action);

/// <summary>
/// Single source of truth for chord -> <see cref="PaneAction"/> bindings.
/// Two consumers read from the same registry:
///
///   1. <see cref="Controls.TerminalControl"/> calls
///      <see cref="Match"/> to decide whether to short-circuit a key
///      event before forwarding it to libghostty (so the chord can
///      reach a window-level KeyboardAccelerator instead of being
///      consumed by the shell).
///   2. <see cref="MainWindow"/> iterates <see cref="All"/> at startup
///      to install one KeyboardAccelerator per binding.
///
/// Adding or changing a binding is one change: edit
/// <see cref="Default"/>. Both consumers pick it up automatically.
///
/// Future work: a config-driven loader will replace
/// <see cref="Default"/> with bindings parsed from the user's
/// ghostty config. The rest of the codebase only sees a
/// <see cref="KeyBindings"/> instance and does not need to change.
/// </summary>
internal sealed class KeyBindings
{
    private readonly List<KeyBinding> _bindings;

    public KeyBindings(IEnumerable<KeyBinding> bindings)
    {
        _bindings = new List<KeyBinding>(bindings);
    }

    public IReadOnlyList<KeyBinding> All => _bindings;

    /// <summary>
    /// Return the action bound to the given chord, or null if no
    /// binding matches. Modifier comparison is exact: a binding for
    /// Ctrl+Shift+D will not match Ctrl+Shift+Alt+D, by design.
    ///
    /// Linear scan is fine at the current ~7 bindings. If the
    /// config-driven loader lands with 50+ bindings, switch to a
    /// Dictionary&lt;(mods,key), action&gt; built once in the ctor.
    /// </summary>
    public PaneAction? Match(VirtualKeyModifiers modifiers, VirtualKey key)
    {
        foreach (var b in _bindings)
        {
            if (b.Modifiers == modifiers && b.Key == key) return b.Action;
        }
        return null;
    }

    /// <summary>
    /// Hardcoded default bindings for PR 2. Mirrors Windows Terminal
    /// muscle memory: Ctrl+Shift+D / E for splits, Ctrl+Shift+W to
    /// close, Alt+Arrows for directional focus.
    /// </summary>
    public static KeyBindings Default { get; } = new(new[]
    {
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.D, PaneAction.SplitVertical),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.E, PaneAction.SplitHorizontal),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.W, PaneAction.ClosePane),
        new KeyBinding(VirtualKeyModifiers.Menu, VirtualKey.Left, PaneAction.FocusLeft),
        new KeyBinding(VirtualKeyModifiers.Menu, VirtualKey.Right, PaneAction.FocusRight),
        new KeyBinding(VirtualKeyModifiers.Menu, VirtualKey.Up, PaneAction.FocusUp),
        new KeyBinding(VirtualKeyModifiers.Menu, VirtualKey.Down, PaneAction.FocusDown),
    });
}
