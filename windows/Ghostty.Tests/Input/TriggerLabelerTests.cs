using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class TriggerLabelerTests
{
    private static EnumeratedKeybind Kb(params KeybindTrigger[] steps)
        => new(steps, "noop", GhosttyBindingFlags.Consumed);

    [Fact]
    public void PhysicalChord_DecodesModsAndKey()
    {
        var kb = Kb(new KeybindTrigger(0, 39, 1u | 2u));
        Assert.Equal("Ctrl+Shift+T", TriggerLabeler.Describe(kb));
    }

    [Fact]
    public void ModOrder_IsCtrlShiftAltWin()
    {
        var kb = Kb(new KeybindTrigger(0, 20, 1u | 2u | 4u | 8u));
        Assert.Equal("Ctrl+Shift+Alt+Win+A", TriggerLabeler.Describe(kb));
    }

    [Fact]
    public void NamedAndArrowKeys_GetFriendlyLabels()
    {
        Assert.Equal("Enter", TriggerLabeler.Describe(Kb(new KeybindTrigger(0, 58, 0))));
        Assert.Equal("Alt+Up", TriggerLabeler.Describe(Kb(new KeybindTrigger(0, 78, 4u))));
        Assert.Equal("F11", TriggerLabeler.Describe(Kb(new KeybindTrigger(0, 131, 0))));
        Assert.Equal("Ctrl+`", TriggerLabeler.Describe(Kb(new KeybindTrigger(0, 1, 2u))));
    }

    [Fact]
    public void UnicodeTrigger_UsesCodepointChar()
    {
        var kb = Kb(new KeybindTrigger(1, 0x3D, 4u));
        Assert.Equal("Alt+=", TriggerLabeler.Describe(kb));
    }

    [Fact]
    public void CatchAll_RendersAny()
    {
        var kb = Kb(new KeybindTrigger(2, 0, 0));
        Assert.Equal("Any", TriggerLabeler.Describe(kb));
    }

    [Fact]
    public void Sequence_JoinsStepsWithSpace()
    {
        var kb = Kb(new KeybindTrigger(0, 30, 2u), new KeybindTrigger(0, 38, 2u));
        Assert.Equal("Ctrl+K Ctrl+S", TriggerLabeler.Describe(kb));
    }

    [Fact]
    public void NoSteps_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TriggerLabeler.Describe(new EnumeratedKeybind(new List<KeybindTrigger>(), "x", GhosttyBindingFlags.None)));
    }
}
