using System.Runtime.InteropServices;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Interop;

public class KeybindInteropLayoutTests
{
    [Fact]
    public void MaxSteps_MatchesAbiContract()
    {
        Assert.Equal(4, KeybindInterop.MaxSteps);
    }

    [Fact]
    public void GhosttyTriggerC_HasExpectedLayout()
    {
        Assert.Equal(12, Marshal.SizeOf<GhosttyTriggerC>());
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttyTriggerC>(nameof(GhosttyTriggerC.Tag)));
        Assert.Equal(4, (int)Marshal.OffsetOf<GhosttyTriggerC>(nameof(GhosttyTriggerC.Key)));
        Assert.Equal(8, (int)Marshal.OffsetOf<GhosttyTriggerC>(nameof(GhosttyTriggerC.Mods)));
    }

    [Fact]
    public void TriggerSteps_IsContiguousArrayOfTriggers()
    {
        Assert.Equal(12 * KeybindInterop.MaxSteps, Marshal.SizeOf<TriggerSteps>());
    }

    [Fact]
    public void GhosttyKeybindC_HasExpectedLayout()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<GhosttyKeybindC>(nameof(GhosttyKeybindC.Steps)));
        Assert.Equal(48, (int)Marshal.OffsetOf<GhosttyKeybindC>(nameof(GhosttyKeybindC.StepCount)));
        Assert.Equal(56, (int)Marshal.OffsetOf<GhosttyKeybindC>(nameof(GhosttyKeybindC.Action)));
        Assert.Equal(64, (int)Marshal.OffsetOf<GhosttyKeybindC>(nameof(GhosttyKeybindC.Flags)));
    }

    [Theory]
    [InlineData(GhosttyBindingFlags.Consumed, 1u)]
    [InlineData(GhosttyBindingFlags.All, 2u)]
    [InlineData(GhosttyBindingFlags.Global, 4u)]
    [InlineData(GhosttyBindingFlags.Performable, 8u)]
    public void BindingFlags_MatchAbiValues(GhosttyBindingFlags flag, uint expected)
    {
        Assert.Equal(expected, (uint)flag);
    }
}
