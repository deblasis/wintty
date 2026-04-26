namespace Ghostty.Core.Input;

/// <summary>
/// Read-only view of the Alt and Shift modifier key state at call time.
/// Production binds to <c>Microsoft.UI.Input.InputKeyboardSource</c>;
/// tests inject <c>FakeModifierKeyState</c>. Introduced for PR 4 so the
/// new-tab split button's modifier-routing matrix can be unit-tested
/// cross-platform without booting a WinUI host.
/// </summary>
public interface IModifierKeyState
{
    bool IsAltDown { get; }
    bool IsShiftDown { get; }
}
