using System;
using Ghostty.Core.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;
using Windows.UI.Core;

namespace Ghostty.Input;

/// <summary>
/// The one signal a surface raises when it consumes Enter, Escape or Space to
/// close itself, or to hand focus back to a pane.
///
/// Marking the KeyDown handled does not stop the key's WM_CHAR: TranslateMessage
/// posted it before the KeyDown was dispatched. Once the surface is gone that
/// character is delivered to whatever holds focus, which can be any pane in the
/// window (a popup that closes around the focused element drops focus on the
/// first focusable one), and the pane forwards it to the shell. Enter's '\r'
/// runs whatever is on the prompt line.
///
/// Every window arms each of its terminals when this is raised. A terminal
/// drops the next character only when it is one of the armed keys', and a
/// KeyDown or IME composition on that terminal retires the arm first, so
/// arming a pane the character never reaches costs nothing.
/// </summary>
internal static class ConsumedCloseKey
{
    /// <summary>
    /// Raised with the keys whose trailing character must not reach a pane.
    /// Each MainWindow subscribes for its lifetime and takes it back on close.
    /// </summary>
    internal static event Action<ConsumedCloseChars>? Raised;

    /// <summary>
    /// A surface's own KeyDown consumed <paramref name="key"/> to close. Call it
    /// before the close runs: the close moves focus inside the same keystroke.
    /// </summary>
    internal static void Raise(VirtualKey key) => Raise(ConsumedCloseArm.ForVirtualKey((int)key));

    /// <summary>Arms for <paramref name="chars"/>; a no-op for none.</summary>
    internal static void Raise(ConsumedCloseChars chars)
    {
        if (chars == ConsumedCloseChars.None) return;
        Raised?.Invoke(chars);
    }

    /// <summary>
    /// For a close whose trigger the caller cannot see: a button's Click, a
    /// list's ItemClick, a framework dialog or flyout. Arms for whichever of
    /// Enter, Escape and Space is down right now. The thread's key state
    /// follows message order, so inside the keystroke that caused the close
    /// the key reads as down, and a mouse-driven close arms nothing.
    /// </summary>
    internal static void RaiseForHeldKeys() => Raise(HeldKeys());

    /// <summary>
    /// For an act with more than one entrance: the key when the caller knows
    /// it (its own KeyDown case), the held keys when it does not (a Click or
    /// ItemClick that a key or a mouse can raise).
    /// </summary>
    internal static void RaiseFor(VirtualKey? key)
    {
        if (key is { } known) Raise(known);
        else RaiseForHeldKeys();
    }

    /// <summary>The close keys that are down right now on this thread.</summary>
    internal static ConsumedCloseChars HeldKeys()
    {
#if TESTSEAM
        if (TestSeamHeldKeys is { } forced) return forced;
#endif
        var chars = ConsumedCloseChars.None;
        if (IsDown(VirtualKey.Enter)) chars |= ConsumedCloseChars.Return;
        if (IsDown(VirtualKey.Escape)) chars |= ConsumedCloseChars.Escape;
        if (IsDown(VirtualKey.Space)) chars |= ConsumedCloseChars.Space;
        return chars;
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    /// <summary>
    /// Watches a framework dialog: Enter runs its default button and Escape
    /// cancels it, both inside the dialog's own KeyDown, and the dialog hands
    /// focus back to the pane it was opened over. Closing fires synchronously
    /// inside that keystroke.
    /// </summary>
    internal static void Watch(ContentDialog dialog) =>
        dialog.Closing += (_, _) => RaiseForHeldKeys();

    /// <summary>
    /// Watches a framework flyout: Escape dismisses it and Enter invokes a
    /// menu item, and either hands focus back to where it was before the
    /// flyout opened, usually a pane.
    /// </summary>
    internal static void Watch(FlyoutBase flyout) =>
        flyout.Closing += (_, _) => RaiseForHeldKeys();

#if TESTSEAM
    /// <summary>
    /// The test seam's stand-in for a held key. The seam drives a close in
    /// process with no key down, so it names the key the close would have
    /// been pressed with. Null outside a seam drive.
    /// </summary>
    internal static ConsumedCloseChars? TestSeamHeldKeys { get; set; }
#endif
}
