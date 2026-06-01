using System;
using System.Runtime.InteropServices;
using System.Text;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Interop;

public class KeybindMapperTests
{
    [Fact]
    public void ToEnumerated_MapsStepsActionAndFlags()
    {
        var actionBytes = Encoding.UTF8.GetBytes("new_split:right\0");
        var handle = GCHandle.Alloc(actionBytes, GCHandleType.Pinned);
        try
        {
            var c = new GhosttyKeybindC
            {
                StepCount = 1,
                Action = handle.AddrOfPinnedObject(),
                Flags = (uint)(GhosttyBindingFlags.Consumed | GhosttyBindingFlags.Performable),
            };
            c.Steps[0] = new GhosttyTriggerC { Tag = 0, Key = 42, Mods = 5 };

            var result = KeybindInterop.ToEnumerated(c);

            Assert.Equal("new_split:right", result.Action);
            Assert.Single(result.Steps);
            Assert.Equal(42u, result.Steps[0].Key);
            Assert.True(result.Flags.HasFlag(GhosttyBindingFlags.Performable));
            Assert.True(result.Flags.HasFlag(GhosttyBindingFlags.Consumed));
        }
        finally
        {
            handle.Free();
        }
    }

    [Fact]
    public void ToEnumerated_ClampsStepCountToMax()
    {
        var c = new GhosttyKeybindC { StepCount = 99, Action = IntPtr.Zero };
        var result = KeybindInterop.ToEnumerated(c);
        Assert.Equal(KeybindInterop.MaxSteps, result.Steps.Count);
        Assert.Equal(string.Empty, result.Action);
    }

    [Fact]
    public void ToEnumerated_PreservesMultiStepSequenceOrder()
    {
        var c = new GhosttyKeybindC { StepCount = 2, Action = IntPtr.Zero };
        c.Steps[0] = new GhosttyTriggerC { Tag = 0, Key = 11, Mods = 1 };
        c.Steps[1] = new GhosttyTriggerC { Tag = 0, Key = 22, Mods = 2 };

        var result = KeybindInterop.ToEnumerated(c);

        Assert.Equal(2, result.Steps.Count);
        Assert.Equal(11u, result.Steps[0].Key);
        Assert.Equal(22u, result.Steps[1].Key);
    }

    [Fact]
    public void ToEnumerated_HandlesZeroSteps()
    {
        var c = new GhosttyKeybindC { StepCount = 0, Action = IntPtr.Zero };
        var result = KeybindInterop.ToEnumerated(c);
        Assert.Empty(result.Steps);
    }
}
