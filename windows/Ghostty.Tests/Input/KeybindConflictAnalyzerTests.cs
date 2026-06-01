using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

public class KeybindConflictAnalyzerTests
{
    private const uint Ctrl = 1u << 1;
    private const uint Shift = 1u << 0;
    private const uint Alt = 1u << 2;

    private static EnumeratedKeybind Kb(uint key, uint mods, int tag = 0,
        GhosttyBindingFlags flags = GhosttyBindingFlags.Consumed, int steps = 1)
    {
        var list = new List<KeybindTrigger>();
        for (var i = 0; i < steps; i++) list.Add(new KeybindTrigger(tag, key, mods));
        return new EnumeratedKeybind(list, "noop", flags);
    }

    [Fact]
    public void PlainCtrlC_Physical_IsTerminalShadow()
    {
        // key_c ordinal = 22 in input.Key.
        var c = KeybindConflictAnalyzer.Analyze(Kb(22, Ctrl));
        Assert.Equal(ConflictKind.TerminalShadow, c.Kind);
        Assert.Contains("Ctrl+C", c.Message);
    }

    [Fact]
    public void CtrlBackslash_Physical_IsShadow()
    {
        // backslash ordinal = 2.
        Assert.Equal(ConflictKind.TerminalShadow, KeybindConflictAnalyzer.Analyze(Kb(2, Ctrl)).Kind);
    }

    [Fact]
    public void CtrlC_Unicode_IsShadow()
    {
        // tag 1 unicode, codepoint 'c'.
        Assert.Equal(ConflictKind.TerminalShadow, KeybindConflictAnalyzer.Analyze(Kb('c', Ctrl, tag: 1)).Kind);
    }

    [Fact]
    public void CtrlShiftC_IsNotShadow()
    {
        Assert.Equal(ConflictKind.None, KeybindConflictAnalyzer.Analyze(Kb(22, Ctrl | Shift)).Kind);
    }

    [Fact]
    public void AltC_IsNotShadow()
    {
        Assert.Equal(ConflictKind.None, KeybindConflictAnalyzer.Analyze(Kb(22, Alt)).Kind);
    }

    [Fact]
    public void CtrlT_NonControlLetter_IsNotShadow()
    {
        // key_t = 39.
        Assert.Equal(ConflictKind.None, KeybindConflictAnalyzer.Analyze(Kb(39, Ctrl)).Kind);
    }

    [Fact]
    public void NonConsumed_IsNotShadow()
    {
        Assert.Equal(ConflictKind.None,
            KeybindConflictAnalyzer.Analyze(Kb(22, Ctrl, flags: GhosttyBindingFlags.Performable)).Kind);
    }

    [Fact]
    public void Sequence_IsNotShadow()
    {
        Assert.Equal(ConflictKind.None, KeybindConflictAnalyzer.Analyze(Kb(22, Ctrl, steps: 2)).Kind);
    }

    [Fact]
    public void None_HasNoConflictFlag()
    {
        Assert.False(KeybindConflictAnalyzer.Analyze(Kb(39, Ctrl)).HasConflict);
        Assert.True(KeybindConflictAnalyzer.Analyze(Kb(22, Ctrl)).HasConflict);
    }
}
