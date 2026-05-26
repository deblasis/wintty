using Ghostty.Core.Theming;
using Xunit;

namespace Ghostty.Tests.Theming;

/// <summary>
/// Unit tests for <see cref="AccentColorResolver"/>. The contract is a
/// strict precedence: explicit accent-color > cursor-color > the
/// palette-derived fallback. Decoupling the chrome accent from the
/// terminal cursor means a vivid cursor (e.g. red) no longer paints
/// the active tab background, focus border, and rail.
/// </summary>
public sealed class AccentColorResolverTests
{
    private const uint Accent = 0x00112233u;
    private const uint Cursor = 0x00FF0000u;
    private const uint Palette = 0x000077AAu;

    [Fact]
    public void Explicit_accent_wins_over_cursor_and_palette()
    {
        var resolved = AccentColorResolver.Resolve(
            accentColor: Accent,
            cursorColor: Cursor,
            paletteFallback: () => Palette);

        Assert.Equal(Accent, resolved);
    }

    [Fact]
    public void Cursor_used_when_accent_unset()
    {
        // Preserves the original "consistent visual accent" intent so
        // users who only set cursor-color get the same chrome they had
        // before accent-color existed.
        var resolved = AccentColorResolver.Resolve(
            accentColor: null,
            cursorColor: Cursor,
            paletteFallback: () => Palette);

        Assert.Equal(Cursor, resolved);
    }

    [Fact]
    public void Palette_fallback_used_when_both_unset()
    {
        var resolved = AccentColorResolver.Resolve(
            accentColor: null,
            cursorColor: null,
            paletteFallback: () => Palette);

        Assert.Equal(Palette, resolved);
    }

    [Fact]
    public void Accent_wins_even_when_cursor_unset()
    {
        // Guards the precedence shape: a future refactor that inverts
        // the null-coalesce order would still pass the first three
        // tests; this one anchors accent-over-palette directly.
        var resolved = AccentColorResolver.Resolve(
            accentColor: Accent,
            cursorColor: null,
            paletteFallback: () => Palette);

        Assert.Equal(Accent, resolved);
    }

    [Fact]
    public void Palette_fallback_not_invoked_when_accent_set()
    {
        // Palette resolution scans 16 colors for saturation; skipping
        // the call when accent-color is set keeps the hot path cheap.
        // We assert lazy evaluation here so future inlining doesn't
        // accidentally start invoking it on every recompute.
        var invoked = false;
        AccentColorResolver.Resolve(
            accentColor: Accent,
            cursorColor: null,
            paletteFallback: () => { invoked = true; return Palette; });

        Assert.False(invoked);
    }

    [Fact]
    public void Palette_fallback_not_invoked_when_cursor_set()
    {
        var invoked = false;
        AccentColorResolver.Resolve(
            accentColor: null,
            cursorColor: Cursor,
            paletteFallback: () => { invoked = true; return Palette; });

        Assert.False(invoked);
    }
}
