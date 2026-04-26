using Ghostty.Core.Input;

namespace Ghostty.Tests.Input;

internal sealed class FakeModifierKeyState : IModifierKeyState
{
    public bool IsAltDown { get; set; }
    public bool IsShiftDown { get; set; }
}
