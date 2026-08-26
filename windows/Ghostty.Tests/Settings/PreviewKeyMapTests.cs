using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// Virtual-key mapping for preview surfaces: which Win32 VK codes the
/// fake DOS shell consumes, and which fall through. Bare keys only, with
/// Ctrl+C as the one chord, mirroring the website's key handler (it
/// ignores every other modified key).
/// </summary>
public class PreviewKeyMapTests
{
    [Theory]
    [InlineData(0x0D, DosShellKey.Enter)]
    [InlineData(0x08, DosShellKey.Backspace)]
    [InlineData(0x26, DosShellKey.Up)]
    [InlineData(0x28, DosShellKey.Down)]
    [InlineData(0x1B, DosShellKey.Escape)]
    [InlineData(0x2D, DosShellKey.Insert)]
    public void BareEditingKeysMap(int virtualKey, DosShellKey expected)
    {
        Assert.True(PreviewKeyMap.TryMap(virtualKey, ctrl: false, shift: false, alt: false, out var key));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void CtrlCMapsToTheDosInterrupt()
    {
        Assert.True(PreviewKeyMap.TryMap(0x43, ctrl: true, shift: false, alt: false, out var key));
        Assert.Equal(DosShellKey.CtrlC, key);
    }

    [Theory]
    [InlineData(0x43)]                     // plain C types a character
    [InlineData(0x0D)]                     // Ctrl+Enter is not a shell key
    [InlineData(0x26)]                     // Shift+Up belongs to the host
    [InlineData(0x1B)]                     // Alt+Escape belongs to the host
    [InlineData(0x51)]                     // Q is text, not a key
    public void ModifiedOrTextualKeysDoNotMap(int virtualKey)
    {
        Assert.False(PreviewKeyMap.TryMap(virtualKey, ctrl: true, shift: true, alt: true, out _));
    }

    [Fact]
    public void ShiftedCDoesNotMap()
    {
        // Shift+C is the capital letter, delivered as a character.
        Assert.False(PreviewKeyMap.TryMap(0x43, ctrl: false, shift: true, alt: false, out _));
    }
}
