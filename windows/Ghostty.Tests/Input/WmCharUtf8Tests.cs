using System;
using System.Text;
using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

/// <summary>
/// WinUI 3 raises CharacterReceived once per UTF-16 code unit. A
/// supplementary-plane scalar (emoji, many CJK ext) therefore arrives
/// as a high surrogate followed by a low surrogate. Encoding each unit
/// with <c>new Rune(ch)</c> throws ArgumentOutOfRangeException and
/// takes down the UI thread — that is the crash this encoder exists to
/// close.
/// </summary>
public class WmCharUtf8Tests
{
    [Fact]
    public void BmpAscii_EncodesOneByte()
    {
        char pending = '\0';
        Span<byte> dest = stackalloc byte[4];
        Assert.True(WmCharUtf8.TryEncode('A', ref pending, dest, out var written));
        Assert.Equal(1, written);
        Assert.Equal((byte)'A', dest[0]);
        Assert.Equal('\0', pending);
    }

    [Fact]
    public void BmpCjk_EncodesThreeBytes()
    {
        char pending = '\0';
        Span<byte> dest = stackalloc byte[4];
        Assert.True(WmCharUtf8.TryEncode('日', ref pending, dest, out var written));
        Assert.Equal(3, written);
        Assert.Equal("日"u8.ToArray(), dest[..written].ToArray());
        Assert.Equal('\0', pending);
    }

    [Fact]
    public void HighSurrogate_HoldsAndWritesNothing()
    {
        char pending = '\0';
        Span<byte> dest = stackalloc byte[4];
        var rocket = "\U0001F680";
        Assert.False(WmCharUtf8.TryEncode(rocket[0], ref pending, dest, out var written));
        Assert.Equal(0, written);
        Assert.Equal(rocket[0], pending);
    }

    [Fact]
    public void HighThenLowSurrogate_EncodesFourUtf8BytesWithoutThrowing()
    {
        char pending = '\0';
        Span<byte> dest = stackalloc byte[4];
        var rocket = "\U0001F680";

        Assert.False(WmCharUtf8.TryEncode(rocket[0], ref pending, dest, out _));
        Assert.True(WmCharUtf8.TryEncode(rocket[1], ref pending, dest, out var written));

        Assert.Equal(4, written);
        Assert.Equal("\U0001F680"u8.ToArray(), dest[..written].ToArray());
        Assert.Equal('\0', pending);
        Assert.Equal("\U0001F680", Encoding.UTF8.GetString(dest[..written]));
    }

    [Fact]
    public void LoneLowSurrogate_DoesNotThrow()
    {
        char pending = '\0';
        Span<byte> dest = stackalloc byte[4];
        var rocket = "\U0001F680";
        Assert.False(WmCharUtf8.TryEncode(rocket[1], ref pending, dest, out var written));
        Assert.Equal(0, written);
        Assert.Equal('\0', pending);
    }

    [Fact]
    public void LoneHighSurrogateReplacedByBmp_DropsTheHigh()
    {
        char pending = '\0';
        Span<byte> dest = stackalloc byte[4];
        var rocket = "\U0001F680";
        Assert.False(WmCharUtf8.TryEncode(rocket[0], ref pending, dest, out _));
        Assert.True(WmCharUtf8.TryEncode('x', ref pending, dest, out var written));
        Assert.Equal(1, written);
        Assert.Equal((byte)'x', dest[0]);
        Assert.Equal('\0', pending);
    }

    [Fact]
    public void C0Control_StillEncodes()
    {
        // Ctrl+A arrives as U+0001. The existing TerminalControl comment
        // is explicit that C0 filtering is libghostty's job.
        char pending = '\0';
        Span<byte> dest = stackalloc byte[4];
        Assert.True(WmCharUtf8.TryEncode('\u0001', ref pending, dest, out var written));
        Assert.Equal(1, written);
        Assert.Equal((byte)1, dest[0]);
    }

    [Fact]
    public void RuneCtor_ThrowsOnHighSurrogate_WhichIsTheProductionBug()
    {
        // Pin the BCL behaviour the UI thread was hitting so a future
        // "just use new Rune(ch)" cannot land again without this going red.
        var rocket = "\U0001F680";
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rune(rocket[0]));
    }
}
