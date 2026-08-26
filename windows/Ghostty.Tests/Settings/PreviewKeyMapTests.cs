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
    private static void MapsTo(int virtualKey, DosShellKey expected)
    {
        Assert.True(PreviewKeyMap.TryMap(virtualKey, ctrl: false, shift: false, alt: false, out var key));
        Assert.Equal(expected, key);
    }

    private static void DoesNotMap(int virtualKey, bool ctrl = false, bool shift = false, bool alt = false)
        => Assert.False(PreviewKeyMap.TryMap(virtualKey, ctrl, shift, alt, out _));

    [Fact]
    public void EnterMaps() => MapsTo(0x0D, DosShellKey.Enter);

    [Fact]
    public void BackspaceMaps() => MapsTo(0x08, DosShellKey.Backspace);

    [Fact]
    public void ArrowUpMaps() => MapsTo(0x26, DosShellKey.Up);

    [Fact]
    public void ArrowDownMaps() => MapsTo(0x28, DosShellKey.Down);

    [Fact]
    public void EscapeMaps() => MapsTo(0x1B, DosShellKey.Escape);

    [Fact]
    public void InsertMaps() => MapsTo(0x2D, DosShellKey.Insert);

    [Fact]
    public void CtrlCMapsToTheDosInterrupt()
    {
        Assert.True(PreviewKeyMap.TryMap(0x43, ctrl: true, shift: false, alt: false, out var key));
        Assert.Equal(DosShellKey.CtrlC, key);
    }

    [Fact]
    public void PlainCDoesNotMapBecauseItIsText() => DoesNotMap(0x43);

    [Fact]
    public void ShiftedCDoesNotMap()
        // Shift+C is the capital letter, delivered as a character.
        => DoesNotMap(0x43, shift: true);

    [Fact]
    public void CtrlEnterDoesNotMap() => DoesNotMap(0x0D, ctrl: true);

    [Fact]
    public void ShiftUpDoesNotMap() => DoesNotMap(0x26, shift: true);

    [Fact]
    public void AltEscapeDoesNotMap() => DoesNotMap(0x1B, alt: true);

    [Fact]
    public void PlainLetterDoesNotMap() => DoesNotMap(0x51);
}
