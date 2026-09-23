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
    public void ACharacterTheArmedKeyDoesNotProduceFlowsAndSpendsTheArm(char ch)
    {
        // Armed for Enter: anything else that arrives first is typing whose
        // KeyDown this surface never saw, and it must flow. It also proves
        // the arm's keystroke went elsewhere, so the arm is spent.
        var arm = new ConsumedCloseArm();
        arm.Arm(ConsumedCloseChars.Return);
        Assert.False(arm.Consume(ch));
        Assert.False(arm.IsArmed);
        Assert.False(arm.Consume('\r'));
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
