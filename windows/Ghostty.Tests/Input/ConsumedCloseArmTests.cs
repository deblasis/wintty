using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

/// <summary>
/// The terminal's one-shot guard for a key another surface consumed to close
/// itself. The decision is pure, so it is pinned here; the wiring that arms
/// and retires it is pinned in ConsumedCloseKeyWiringTests.
/// </summary>
public class ConsumedCloseArmTests
{
    [Theory]
    [InlineData(ConsumedCloseChars.Return, '\r')]
    [InlineData(ConsumedCloseChars.Return, '\n')]
    [InlineData(ConsumedCloseChars.Escape, '\u001b')]
    [InlineData(ConsumedCloseChars.Space, ' ')]
    public void AnArmedKeysCharacterIsDropped(ConsumedCloseChars armed, char ch)
    {
        var arm = new ConsumedCloseArm();
        arm.Arm(armed);
        Assert.True(arm.Consume(ch));
        Assert.False(arm.IsArmed);
    }

    [Fact]
    public void TheArmIsOneShot()
    {
        var arm = new ConsumedCloseArm();
        arm.Arm(ConsumedCloseChars.Return);
        Assert.True(arm.Consume('\r'));
        // A second Enter is the user's own and must reach the shell.
        Assert.False(arm.Consume('\r'));
    }

    [Theory]
    [InlineData('a')]
    [InlineData('\u001b')]
    [InlineData(' ')]
    public void ACharacterTheArmedKeyDoesNotProduceFlowsAndLeavesTheArm(char ch)
    {
        // Armed for Enter: anything else flows. It does not spend the arm,
        // because a dead key's accent can arrive ahead of the '\r' that is
        // still to be dropped; the pane's next KeyDown retires a stale arm.
        var arm = new ConsumedCloseArm();
        arm.Arm(ConsumedCloseChars.Return);
        Assert.False(arm.Consume(ch));
        Assert.True(arm.IsArmed);
        Assert.True(arm.Consume('\r'));
    }

    [Fact]
    public void ADeadKeysAccentBeforeTheArmedCharacterDoesNotSpendTheArm()
    {
        // A dead key pending when Enter closes a surface: TranslateMessage
        // posts the spacing accent and then '\r'. The accent is not the
        // armed key's character, so it must not spend the arm, or the '\r'
        // behind it reaches the shell and submits the line.
        var arm = new ConsumedCloseArm();
        arm.Arm(ConsumedCloseChars.Return);
        Assert.False(arm.Consume('\''));
        Assert.True(arm.Consume('\r'));
    }

    [Fact]
    public void TheArmAndTheChordSuppressAreSpentByTheSameCharacter()
    {
        // A swallowed chord on a close key (Ctrl+Shift+Space) whose action
        // raises the signal: both drops stand for the chord's one character.
        // If the arm returned first, the chord suppress would survive and
        // eat the next thing the user types.
        var arm = new ConsumedCloseArm();
        arm.Arm(ConsumedCloseChars.Space);
        var suppress = true;
        Assert.Equal(CharacterFate.DroppedByArm, ConsumedCloseArm.Decide(ref arm, ref suppress, ' '));
        Assert.False(suppress);
        Assert.False(arm.IsArmed);
        Assert.Equal(CharacterFate.Forward, ConsumedCloseArm.Decide(ref arm, ref suppress, 'l'));
    }

    [Fact]
    public void TheChordSuppressAloneDropsOneCharacter()
    {
        var arm = new ConsumedCloseArm();
        var suppress = true;
        Assert.Equal(CharacterFate.DroppedByChordSuppress, ConsumedCloseArm.Decide(ref arm, ref suppress, '\u0005'));
        Assert.False(suppress);
        Assert.Equal(CharacterFate.Forward, ConsumedCloseArm.Decide(ref arm, ref suppress, 'a'));
    }

    [Fact]
    public void RetireClearsAStandingArmAndReportsIt()
    {
        var arm = new ConsumedCloseArm();
        Assert.False(arm.Retire());
        arm.Arm(ConsumedCloseChars.Escape);
        Assert.True(arm.Retire());
        Assert.False(arm.IsArmed);
        Assert.False(arm.Consume('\u001b'));
    }

    [Fact]
    public void ArmsAccumulateUntilSpent()
    {
        var arm = new ConsumedCloseArm();
        arm.Arm(ConsumedCloseChars.Return);
        arm.Arm(ConsumedCloseChars.Space);
        Assert.Equal(ConsumedCloseChars.Return | ConsumedCloseChars.Space, arm.Armed);
        Assert.True(arm.Consume(' '));
        Assert.False(arm.IsArmed);
    }

    [Fact]
    public void AnUnarmedSurfaceDropsNothing()
    {
        var arm = new ConsumedCloseArm();
        foreach (var ch in "\r\n\u001b a")
            Assert.False(arm.Consume(ch));
    }

    [Theory]
    [InlineData(0x0D, ConsumedCloseChars.Return)]
    [InlineData(0x1B, ConsumedCloseChars.Escape)]
    [InlineData(0x20, ConsumedCloseChars.Space)]
    [InlineData(0x41, ConsumedCloseChars.None)]
    [InlineData(0x09, ConsumedCloseChars.None)]
    public void TheCloseKeysMapToTheirCharacters(int virtualKey, ConsumedCloseChars expected)
    {
        Assert.Equal(expected, ConsumedCloseArm.ForVirtualKey(virtualKey));
    }
}
