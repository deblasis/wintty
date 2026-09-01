using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Input;

/// <summary>
/// The frame's own answer to "is this chord bound?". It runs with no
/// surface in the focus chain, so nothing downstream re-checks it: whatever
/// this returns is dispatched. An over-match here is a key taken from
/// whatever the user aimed it at.
/// </summary>
public class FrameChordMatcherTests
{
    private const int VkT = 0x54;
    private const int VkA = 0x41;

    private const uint ModShift = 1u << 0;
    private const uint ModCtrl = 1u << 1;
    private const uint ModAlt = 1u << 2;
    private const uint ModSuper = 1u << 3;
    private const uint ModCtrlRight = 1u << 7;
    private const uint ModSuperRight = 1u << 9;

    private static uint Physical(int virtualKey)
        => ChordEncoder.TryEncode(virtualKey, false, false, false, false)!.Value.Key;

    private static EnumeratedKeybind Bind(string action, int tag, uint key, uint mods)
        => new([new KeybindTrigger(tag, key, mods)], action, GhosttyBindingFlags.Consumed);

    private static EnumeratedKeybind PhysicalBind(string action, int virtualKey, uint mods)
        => Bind(action, (int)GhosttyTriggerTag.Physical, Physical(virtualKey), mods);

    private static List<EnumeratedKeybind> Binds(params EnumeratedKeybind[] binds) => [.. binds];

    [Fact]
    public void ABoundChord_ReturnsItsAction()
    {
        var binds = Binds(PhysicalBind("new_tab", VkT, ModCtrl));

        Assert.Equal(
            "new_tab",
            FrameChordMatcher.Match(binds, VkT, ctrl: true, shift: false, alt: false, win: false));
    }

    [Fact]
    public void AnUnboundChord_ReturnsNull()
    {
        var binds = Binds(PhysicalBind("new_tab", VkT, ModCtrl));

        Assert.Null(
            FrameChordMatcher.Match(binds, VkA, ctrl: true, shift: false, alt: false, win: false));
    }

    // The chord the user pressed is not the chord that was bound. Win+Ctrl+T
    // is not OS-reserved, so it reaches the handler; before the modifier was
    // reported it arrived indistinguishable from Ctrl+T and opened a tab.
    [Fact]
    public void TheWindowsKey_DoesNotMatchABindThatDidNotAskForIt()
    {
        var binds = Binds(PhysicalBind("new_tab", VkT, ModCtrl));

        Assert.Null(
            FrameChordMatcher.Match(binds, VkT, ctrl: true, shift: false, alt: false, win: true));
    }

    [Fact]
    public void ASuperBind_MatchesOnlyWhenTheWindowsKeyIsHeld()
    {
        var binds = Binds(PhysicalBind("new_window", VkT, ModCtrl | ModSuper));

        Assert.Equal(
            "new_window",
            FrameChordMatcher.Match(binds, VkT, ctrl: true, shift: false, alt: false, win: true));
        Assert.Null(
            FrameChordMatcher.Match(binds, VkT, ctrl: true, shift: false, alt: false, win: false));
    }

    // An extra modifier makes a different chord, in both directions.
    [Fact]
    public void ModifiersCompareExactly()
    {
        var binds = Binds(PhysicalBind("new_tab", VkT, ModCtrl));

        Assert.Null(
            FrameChordMatcher.Match(binds, VkT, ctrl: true, shift: true, alt: false, win: false));
        Assert.Null(
            FrameChordMatcher.Match(binds, VkT, ctrl: true, shift: false, alt: true, win: false));
        Assert.Null(
            FrameChordMatcher.Match(binds, VkT, ctrl: false, shift: false, alt: false, win: false));
    }

    // A binding written with a right-hand modifier names the same modifier a
    // key event reports, so it still answers.
    [Fact]
    public void RightHandModifiers_NameTheSameModifier()
    {
        var binds = Binds(
            PhysicalBind("new_tab", VkT, ModCtrlRight),
            PhysicalBind("new_window", VkA, ModCtrlRight | ModSuperRight));

        Assert.Equal(
            "new_tab",
            FrameChordMatcher.Match(binds, VkT, ctrl: true, shift: false, alt: false, win: false));
        Assert.Equal(
            "new_window",
            FrameChordMatcher.Match(binds, VkA, ctrl: true, shift: false, alt: false, win: true));
    }

    // The parser stores the UNSHIFTED character, so `ctrl+shift+a` binds 'a'.
    [Fact]
    public void AUnicodeTrigger_MatchesTheUnshiftedCodepoint()
    {
        var binds = Binds(Bind("select_all", (int)GhosttyTriggerTag.Unicode, 'a', ModCtrl | ModShift));

        Assert.Equal(
            "select_all",
            FrameChordMatcher.Match(binds, VkA, ctrl: true, shift: true, alt: false, win: false));
    }

    // A leader sequence needs a pending-trigger state machine the frame does
    // not have. Matching its first step alone would fire the action on a
    // chord the user had not finished typing.
    [Fact]
    public void ALeaderSequence_IsNotMatchedByItsFirstStep()
    {
        var leader = new EnumeratedKeybind(
            [
                new KeybindTrigger((int)GhosttyTriggerTag.Physical, Physical(VkT), ModCtrl),
                new KeybindTrigger((int)GhosttyTriggerTag.Physical, Physical(VkA), ModCtrl),
            ],
            "new_tab",
            GhosttyBindingFlags.Consumed);

        Assert.Null(
            FrameChordMatcher.Match(Binds(leader), VkT, ctrl: true, shift: false, alt: false, win: false));
    }

    [Fact]
    public void AKeyWithNoNameAndNoCodepoint_MatchesNothing()
    {
        // VK_LBUTTON: not in the encoder's table and not a character.
        var binds = Binds(PhysicalBind("new_tab", VkT, ModCtrl));

        Assert.Null(
            FrameChordMatcher.Match(binds, 0x01, ctrl: true, shift: false, alt: false, win: false));
    }

    [Fact]
    public void AnEmptyBindSet_MatchesNothing()
    {
        Assert.Null(
            FrameChordMatcher.Match([], VkT, ctrl: true, shift: false, alt: false, win: false));
    }

    [Fact]
    public void AnAltBind_IsDistinctFromACtrlBind()
    {
        var binds = Binds(
            PhysicalBind("ctrl_action", VkT, ModCtrl),
            PhysicalBind("alt_action", VkT, ModAlt));

        Assert.Equal(
            "alt_action",
            FrameChordMatcher.Match(binds, VkT, ctrl: false, shift: false, alt: true, win: false));
    }
}
