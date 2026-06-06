using System.Collections.Generic;
using System.Text;
using Ghostty.Core.Input;
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
/// The Windows-only residual chord table. libghostty owns matching for
/// every standard action (splits, tabs, fullscreen, scrollback, prompt
/// navigation) through the curated Windows defaults in
/// <c>Config.zig</c>; those chords match inside libghostty and surface
/// back through <c>GhosttyHost.PaneActionRequested</c>.
///
/// What remains here are the chords that have no libghostty action, so
/// the apprt has to match them itself:
/// <see cref="PaneAction.OpenSearch"/> (shows the search-bar widget,
/// which libghostty has no concept of),
/// <see cref="PaneAction.ToggleVerticalTabsPinned"/>,
/// <see cref="PaneAction.ToggleTabLayout"/>, and the nine profile slots.
///
/// <see cref="Controls.TerminalControl"/> calls <see cref="Match"/> on
/// each key event; a hit is dispatched through the same
/// <c>PaneActionRouter</c> path as the libghostty-matched actions and
/// the key is not forwarded to libghostty.
///
/// <see cref="Label"/> / <see cref="Find"/> still answer "what chord
/// runs this action?" for the command palette and settings, but only
/// for these residual actions; chord labels for libghostty-owned
/// actions return once the keybind-enumeration ABI is wired.
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
    /// Render the binding for <paramref name="action"/> as a
    /// human-readable chord (e.g. <c>Ctrl+Shift+,</c>), or
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
            sb.Append(KeyDisplayName(b.Key));
            return sb.ToString();
        }
        return null;
    }

    /// <summary>
    /// Return the action bound to the given chord, or null if no binding
    /// matches. Modifier comparison is exact: a binding for Ctrl+Shift+F
    /// will not match Ctrl+Shift+Alt+F, by design. Linear scan over the
    /// ~12 residual bindings.
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
    /// Return the first <see cref="KeyBinding"/> bound to
    /// <paramref name="action"/>, or null if no chord points to it.
    /// Linear scan; same complexity as <see cref="Match"/> and
    /// <see cref="Label"/>. Single source for the
    /// "what chord opens this action?" lookup so command-palette sources
    /// (BuiltInCommandSource, ProfileCommandSource, ...) do not each
    /// reimplement the loop.
    /// </summary>
    public KeyBinding? Find(PaneAction action)
    {
        foreach (var b in _bindings)
        {
            if (b.Action == action) return b;
        }
        return null;
    }

    /// <summary>
    /// The residual Windows-only bindings: chords with no libghostty
    /// action, which the apprt therefore matches itself. Everything else
    /// (splits, tabs, fullscreen, zoom, equalize, scrollback, prompt
    /// navigation, quake, command palette) now lives in the curated
    /// Windows defaults in <c>Config.zig</c> and is matched by libghostty.
    /// </summary>
    public static KeyBindings WindowsOnly { get; } = new(new[]
    {
        // Open the in-pane search bar. libghostty's start_search only
        // reports the needle back (OnSearchStarted); showing the search
        // widget is purely an apprt concern, so the chord stays here.
        // Ctrl+Shift+F matches Windows Terminal muscle memory; plain
        // Ctrl+F is reserved for in-find inside the Settings raw editor.
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.F, PaneAction.OpenSearch),

        // Vertical-tabs pinned toggle. Only meaningful when vertical-tabs
        // is enabled; PaneActionRouter no-ops if the host is not a
        // VerticalTabHost.
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Space, PaneAction.ToggleVerticalTabsPinned),

        // Runtime switch between horizontal and vertical tab layouts
        // (Ctrl+Shift+, -- same chord as Edge's vertical tabs toggle).
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (VirtualKey)188, PaneAction.ToggleTabLayout),

        // Profiles. Slot N = Profiles[N-1] (post hidden filter, ordered by
        // profile-order). Out-of-range slots silently no-op in the router.
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Number1, PaneAction.OpenProfile1),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Number2, PaneAction.OpenProfile2),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Number3, PaneAction.OpenProfile3),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Number4, PaneAction.OpenProfile4),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Number5, PaneAction.OpenProfile5),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Number6, PaneAction.OpenProfile6),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Number7, PaneAction.OpenProfile7),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Number8, PaneAction.OpenProfile8),
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Number9, PaneAction.OpenProfile9),

        // Ctrl+Shift+/ ("Ctrl+?") opens the keyboard-shortcuts cheat sheet.
        // VK 191 = VK_OEM_2 ('/') on US-ANSI.
        new KeyBinding(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, (VirtualKey)191, PaneAction.ShowKeybindCheatsheet),
    });

    /// <summary>
    /// Human-readable name for a <see cref="VirtualKey"/>. Named enum
    /// members use ToString(); OEM keys that have no named member fall
    /// back to a lookup table so tooltips show "," instead of "188".
    /// Also reused by the command palette's shortcut subtitles so chord
    /// text is rendered from one OEM-aware table.
    /// </summary>
    internal static string KeyDisplayName(VirtualKey key) => (int)key switch
    {
        // Top-row digits Number0..Number9 (VirtualKey values 0x30..0x39).
        // Default ToString() prints "Number1"; we want "1" for chord labels.
        48 => "0",
        49 => "1",
        50 => "2",
        51 => "3",
        52 => "4",
        53 => "5",
        54 => "6",
        55 => "7",
        56 => "8",
        57 => "9",
        // OEM punctuation that lacks a named VirtualKey member.
        188 => ",",
        190 => ".",
        186 => ";",
        191 => "/",
        219 => "[",
        221 => "]",
        220 => "\\",
        192 => "`",
        189 => "-",
        187 => "=",
        222 => "'",
        _ => key.ToString(),
    };
}
