using Ghostty.Core.Input;
using Ghostty.Panes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Ghostty;

// Chord routing for the frame -- the title bar, the tab strip, and the
// empty chrome around them.
//
// Every chord path in the shell begins at a focused TerminalControl: its
// KeyDown handler matches the Windows-only residual table itself and
// forwards everything else to libghostty through the pane's surface, which
// is where libghostty does its own matching. Focus on the frame is outside
// both, so a chord pressed there reached no matcher at all and did nothing.
//
// The routing added here is a bubble-phase handler on the window content:
// it sees a key only after the focused element has declined it, so it can
// never pre-empt the terminal, the strip's own arrow navigation, or a text
// box. It is the last hop the frame was missing, not a new accelerator
// layer above the pane.
public sealed partial class MainWindow
{
    /// <summary>Where keyboard focus sits, as the chord router reads it.</summary>
    internal enum FrameChordFocus
    {
        /// <summary>Nothing in this window holds focus.</summary>
        None,

        /// <summary>Inside a terminal pane; the terminal owns its keys.</summary>
        Pane,

        /// <summary>An editable control; typing into it is not a chord.</summary>
        TextEntry,

        /// <summary>
        /// An overlay is up -- the command palette, the tab switcher, the
        /// overview. Whatever it does with the key, the frame underneath it
        /// does not act on it.
        /// </summary>
        Overlay,

        /// <summary>The frame: title bar, tab strip, or chrome.</summary>
        Frame,
    }

    private int _frameRoutedKeyDowns;

    private void WireFrameChordRouting()
    {
        if (Content is UIElement root) root.KeyDown += OnFrameChordKeyDown;
    }

    private void OnFrameChordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Counted before any gate, and before the Handled check: the seam's
        // probe asks whether the framework delivered the key here at all,
        // which is a different question from whether we acted on it.
        _frameRoutedKeyDowns++;
        if (_isClosed || e.Handled) return;
        if (TryDispatchFrameChord(
                (int)e.Key, Controls.TerminalControl.CurrentChordModifiers()))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Dispatch one chord on behalf of the frame, reporting whether anything
    /// was invoked. Refuses when the focused element owns the key, and when
    /// the key is not one the frame may claim, so a key something else
    /// wanted is never taken from it.
    ///
    /// Modifiers are a parameter rather than a live keyboard read so the
    /// test seam can drive this exact function without synthesizing OS
    /// input; the KeyDown handler above passes the live state.
    /// </summary>
    internal bool TryDispatchFrameChord(int virtualKey, VirtualKeyModifiers mods)
    {
        // Refuse whenever something OWNS the key. That is the rule; "focus
        // is on the frame" is only its common case, and None is claimable
        // for the same reason Frame is: nothing is there to take it from.
        // (No click probed here ever produced None -- a click on the bare
        // caption leaves focus exactly where it was, and a click on empty
        // strip chrome hands it to the strip.)
        //
        // This gate is also what keeps a key from being answered TWICE.
        // libghostty leaves some bindings unhandled on the way back, so the
        // key still bubbles out of the pane to this handler; without the
        // gate, the pane path and this one both fire and one press makes
        // two tabs -- the double-dispatch that got KeyboardAccelerators
        // removed in issue #165.
        //
        // Written as an allow-list, so a focus kind added later refuses
        // until someone decides otherwise. A router that claims keys is the
        // wrong thing to leave open by default.
        if (_isClosed) return false;
        if (CurrentFrameChordFocus() is not (FrameChordFocus.Frame or FrameChordFocus.None))
            return false;
        if (!IsFrameChordShape(virtualKey, mods)) return false;

        // Residual table first, the same order TerminalControl uses. Two
        // chords are claimed on both sides -- ctrl+shift+f is OpenSearch
        // here and start_search in libghostty -- and only the apprt one
        // can show the widget, so the apprt one wins.
        if (Input.KeyBindings.WindowsOnly.Match(mods, (VirtualKey)virtualKey)
            is { } residual)
        {
            _router.Invoke(residual);
            return true;
        }

        // Everything else libghostty owns. Re-read the parsed keybind set
        // each time rather than caching it: this runs only on a modified
        // key with the frame focused, and a cache would need its own
        // config-reload subscription to unwire at window close.
        var action = FrameChordMatcher.Match(
            Interop.KeybindEnumerator.Enumerate(_configService.ConfigHandle),
            virtualKey,
            mods.HasFlag(VirtualKeyModifiers.Control),
            mods.HasFlag(VirtualKeyModifiers.Shift),
            mods.HasFlag(VirtualKeyModifiers.Menu),
            mods.HasFlag(VirtualKeyModifiers.Windows));
        if (action is null) return false;

        // The active surface performs it, which is also how the command
        // palette dispatches: libghostty runs the binding and sends the
        // apprt action back through PaneActionRequested into the router.
        return TryExecuteBindingAction(action);
    }

    /// <summary>
    /// Which keys the frame may claim. Ctrl, Alt and Win are modifiers no
    /// focus navigation and no text entry uses, and a function key is not
    /// text either. Everything else -- letters, digits, arrows, Tab, Space,
    /// Enter, Escape, the paging cluster, with or without Shift -- belongs
    /// to whatever holds focus, so the frame never looks at it. That is the
    /// whole guard against a chord router that eats typing.
    /// </summary>
    private static bool IsFrameChordShape(int virtualKey, VirtualKeyModifiers mods)
        => (mods & (VirtualKeyModifiers.Control
                    | VirtualKeyModifiers.Menu
                    | VirtualKeyModifiers.Windows)) != 0
        || virtualKey is >= 0x70 and <= 0x87; // F1..F24

    /// <summary>
    /// Whether an overlay is up. Asked as a question about STATE rather
    /// than about focus, because the two come apart: an overview can be
    /// open with focus on nothing at all, and reading focus alone would
    /// then report None -- which the router treats as claimable, and
    /// Ctrl+W would close a tab whose tile is still on screen.
    /// </summary>
    private bool AnyOverlayOpen()
        => CommandPalettePopup.IsOpen
        || TabSwitcherPopupHost.IsOpen
        || TabOverviewHost.IsOpen;

    /// <summary>
    /// Read focus the way the router needs it. A terminal pane is found by
    /// walking up from the focused element, so the pane's own children --
    /// the IME sink and the search bar -- count as pane too. An editable
    /// control is always the focus target itself, so that one is a direct
    /// test.
    /// </summary>
    /// <remarks>
    /// An overlay is answered two ways, because either alone leaves a hole.
    /// The state check catches an open overlay holding no focus. The walk
    /// catches focus inside one: an overlay lives in a Popup, whose child is
    /// parented to the XamlRoot's popup root rather than under
    /// <see cref="Window.Content"/>, so a walk that never reaches Content
    /// started somewhere that is not the frame. Testing for reachability
    /// rather than naming the three popups means a fourth is covered the day
    /// it is added.
    /// </remarks>
    internal FrameChordFocus CurrentFrameChordFocus()
    {
        if (Content?.XamlRoot is not { } root) return FrameChordFocus.None;
        if (AnyOverlayOpen()) return FrameChordFocus.Overlay;
        if (FocusManager.GetFocusedElement(root) is not DependencyObject focused)
            return FrameChordFocus.None;

        var inFrame = false;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Controls.TerminalControl) return FrameChordFocus.Pane;
            if (ReferenceEquals(node, Content)) { inFrame = true; break; }
        }

        if (focused is TextBox or RichEditBox or AutoSuggestBox or PasswordBox)
            return FrameChordFocus.TextEntry;

        return inFrame ? FrameChordFocus.Frame : FrameChordFocus.Overlay;
    }

    // ---- test seam accessors (WINTTY_TEST_SEAM=1) --------------------

    /// <summary>
    /// How many KeyDown events the window content has raised since launch.
    /// The probe's evidence that the framework hop happened, independent of
    /// whether the router claimed anything.
    /// </summary>
    internal int TestSeamRoutedKeyDowns => _frameRoutedKeyDowns;

    internal string TestSeamFocusLocation => CurrentFrameChordFocus() switch
    {
        FrameChordFocus.Pane => "pane",
        FrameChordFocus.TextEntry => "text-entry",
        FrameChordFocus.Overlay => "overlay",
        FrameChordFocus.Frame => "frame",
        _ => "none",
    };

    /// <summary>
    /// Put focus on the frame: the first focusable element of the active
    /// tab host, which is a real tab row -- where a click on the strip
    /// leaves it.
    /// </summary>
    internal bool TestSeamFocusFrame()
        => FocusManager.FindFirstFocusableElement(_tabHost.HostElement) is Control control
        && control.Focus(FocusState.Programmatic)
        && CurrentFrameChordFocus() == FrameChordFocus.Frame;

    internal bool TestSeamFocusPane()
        => _tabManager.ActiveTab?.PaneHost?.ActiveLeaf?.Terminal() is { } terminal
        && terminal.Focus(FocusState.Programmatic)
        && CurrentFrameChordFocus() == FrameChordFocus.Pane;

    internal bool TestSeamFrameChord(int virtualKey, VirtualKeyModifiers mods)
        => TryDispatchFrameChord(virtualKey, mods);
}
