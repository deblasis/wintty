using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeyboardMapModelTests
{
    private const uint Shift = 1u << 0, Ctrl = 1u << 1;

    private static EnumeratedKeybind Bind(string keyName, uint mods, string action, int extraSteps = 0)
    {
        var steps = new List<KeybindTrigger>
        {
            new(0, (uint)KeyNames.OrdinalOf(keyName)!.Value, mods),
        };
        for (var i = 0; i < extraSteps; i++)
            steps.Add(new KeybindTrigger(0, (uint)KeyNames.OrdinalOf("key_s")!.Value, 0));
        return new EnumeratedKeybind(steps, action, GhosttyBindingFlags.Consumed);
    }

    [Fact]
    public void LightsKeysOnSelectedLayerOnly()
    {
        var binds = new[]
        {
            Bind("key_t", Ctrl | Shift, "new_split:right"),
            Bind("key_n", Ctrl, "new_tab"),
        };
        var model = KeyboardMapModel.Build(binds, defaults: null, modifierMask: Ctrl | Shift);

        var t = model.ForKey("key_t");
        Assert.NotNull(t);
        Assert.True(t!.IsBound);
        Assert.Equal("new_split:right", t.RawAction);
        Assert.Equal("Split Right", t.ActionLabel);
        Assert.Null(model.ForKey("key_n"));
    }

    [Fact]
    public void CollapsesRightSideModifiers()
    {
        var binds = new[] { Bind("key_t", (1u << 7) | (1u << 6), "new_tab") };
        var model = KeyboardMapModel.Build(binds, null, Ctrl | Shift);
        Assert.NotNull(model.ForKey("key_t"));
    }

    [Fact]
    public void MultiStepBindFlaggedAndKeyedToFirstStep()
    {
        var binds = new[] { Bind("key_k", Ctrl, "new_split:right", extraSteps: 1) };
        var model = KeyboardMapModel.Build(binds, null, Ctrl);
        var k = model.ForKey("key_k");
        Assert.NotNull(k);
        Assert.True(k!.IsMultiStep);
    }

    [Fact]
    public void ClassifiesUserVsDefault()
    {
        var defaults = new[] { Bind("key_t", Ctrl, "new_tab") };
        var binds = new[]
        {
            Bind("key_t", Ctrl, "new_tab"),
            Bind("key_y", Ctrl, "new_window"),
        };
        var model = KeyboardMapModel.Build(binds, defaults, Ctrl);
        Assert.Equal(KeybindSource.Default, model.ForKey("key_t")!.Source);
        Assert.Equal(KeybindSource.User, model.ForKey("key_y")!.Source);
    }

    [Fact]
    public void SkipsNonPhysicalSteps()
    {
        var uni = new EnumeratedKeybind(new[] { new KeybindTrigger(1, (uint)'a', Ctrl) },
                                        "new_tab", GhosttyBindingFlags.Consumed);
        var model = KeyboardMapModel.Build(new[] { uni }, null, Ctrl);
        Assert.Null(model.ForKey("key_a"));
    }

    [Fact]
    public void BareKeyLayer_LightsUnmodifiedKeysOnly()
    {
        var binds = new[]
        {
            Bind("f11", 0, "toggle_fullscreen"),   // bare key -> layer 0
            Bind("key_t", Ctrl, "new_tab"),        // modified -> must NOT leak onto layer 0
        };
        var model = KeyboardMapModel.Build(binds, defaults: null, modifierMask: 0);

        var f11 = model.ForKey("f11");
        Assert.NotNull(f11);
        Assert.Equal("toggle_fullscreen", f11!.RawAction);
        Assert.Null(model.ForKey("key_t"));        // modified bind absent from bare layer
    }
}
