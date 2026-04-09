using System.Collections.Generic;
using System.Text;
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
    /// <summary>
    /// Render the binding for <paramref name="action"/> as a
    /// human-readable chord (e.g. <c>Ctrl+Shift+Alt+V</c>), or
    /// <c>null</c> if no binding exists. Used for tooltips so the
    /// chord text and the KeyboardAccelerator are sourced from the
    /// same place.
    /// </summary>
    public string? Label(PaneAction action)
    {
        foreach (var b in _bindings)
        {
            if (b.Action != action) continue;
            var sb = new StringBuilder();
            if ((b.Modifiers & VirtualKeyModifiers.Control) != 0) sb.Append("Ctrl+");
            if ((b.Modifiers & VirtualKeyModifiers.Shift) != 0)   sb.Append("Shift+");
            if ((b.Modifiers & VirtualKeyModifiers.Menu) != 0)    sb.Append("Alt+");
            if ((b.Modifiers & VirtualKeyModifiers.Windows) != 0) sb.Append("Win+");
            sb.Append(b.Key);
            return sb.ToString();
        }
        return null;
    }

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
        // Panes (#163)
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.D, PaneAction.SplitVertical),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.E, PaneAction.SplitHorizontal),
        new KeyBinding(VirtualKeyModifiers.Menu, VirtualKey.Left, PaneAction.FocusLeft),
        new KeyBinding(VirtualKeyModifiers.Menu, VirtualKey.Right, PaneAction.FocusRight),
        new KeyBinding(VirtualKeyModifiers.Menu, VirtualKey.Up, PaneAction.FocusUp),
        new KeyBinding(VirtualKeyModifiers.Menu, VirtualKey.Down, PaneAction.FocusDown),

        // Tabs (this PR). Ctrl+Shift+W is now CloseActiveProgressive
        // (pane -> tab -> window with confirmation), no longer plain ClosePane.
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.T, PaneAction.NewTab),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.W, PaneAction.CloseActiveProgressive),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.PageDown, PaneAction.NextTab),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.PageUp, PaneAction.PrevTab),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.Number1, PaneAction.JumpTab1),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.Number2, PaneAction.JumpTab2),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.Number3, PaneAction.JumpTab3),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.Number4, PaneAction.JumpTab4),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.Number5, PaneAction.JumpTab5),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.Number6, PaneAction.JumpTab6),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.Number7, PaneAction.JumpTab7),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.Number8, PaneAction.JumpTab8),
        new KeyBinding(VirtualKeyModifiers.Control, VirtualKey.Number9, PaneAction.JumpTabLast),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.PageDown, PaneAction.MoveTabRight),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.PageUp, PaneAction.MoveTabLeft),
        // Vertical tabs (plan 2). Only meaningful when vertical-tabs
        // is enabled; PaneActionRouter no-ops if the host is not a
        // VerticalTabHost.
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Space, PaneAction.ToggleVerticalTabsPinned),
        // Runtime switch between horizontal and vertical tab layouts.
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Menu, VirtualKey.V, PaneAction.ToggleTabLayout),
    });
}
