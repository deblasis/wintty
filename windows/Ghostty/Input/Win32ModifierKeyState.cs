using Ghostty.Core.Input;
using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace Ghostty.Input;

/// <summary>
/// Production <see cref="IModifierKeyState"/> backed by
/// <see cref="InputKeyboardSource.GetKeyStateForCurrentThread"/>.
/// Property reads dispatch into the per-thread input source, so
/// instances must be accessed from the UI thread (which click
/// handlers always are).
///
/// <see cref="VirtualKey.Menu"/> is the Windows name for the Alt key.
/// </summary>
internal sealed class Win32ModifierKeyState : IModifierKeyState
{
    public bool IsAltDown =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    public bool IsShiftDown =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
}
