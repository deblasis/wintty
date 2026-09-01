using System.Reflection;
using Ghostty.Core.Windows;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// The cheap, deterministic half of the contrast guarantee: the chrome ink
/// the shell DERIVES from the built-in theme pair, scored against the
/// background that pair sets.
///
/// The zig side already holds the built-in palette to WCAG in
/// <c>src/config/wintty_theme_test.zig</c>. What it cannot see is what the
/// Windows chrome then does with those colours: unselected strip titles are
/// muted to 70% alpha and composited onto the strip, so the colour a reader
/// sees is a blend, not the pole. That blend is arithmetic, it is
/// deterministic, and it is where a 2.37:1 title came from once already
/// (see InactiveTabInkContrastTests).
///
/// This does NOT replace the rendered oracle
/// (<c>windows/scripts/contrast-oracle.ps1</c>). It cannot: the regression
/// that put a selected-row title at 1.11:1 was a code path that never ran,
/// and every value this file checks was correct while that shipped. This is
/// the layer that fails in CI in milliseconds; the pixel harness is the
/// layer that catches a correct value nothing paints with.
/// </summary>
public sealed class BuiltinThemeChromeContrastTests
{
    private const string ThemeResource = "Ghostty.Tests.Config.Defaults.wintty_theme.zig";

    /// <summary>
    /// WCAG 2.1 SC 1.4.3 for normal-size text: the same 4.5 the palette is
    /// held to in wintty_theme_test.zig, so the two oracles agree on what a
    /// legible title means.
    /// </summary>
    private const double WcagAaText = 4.5;

    /// <summary>
    /// The de-emphasis unselected rows are drawn at
    /// (VerticalTabStrip.InactiveInkAlpha).
    /// </summary>
    private const byte InactiveInkAlpha = 0xB3;

    private const uint White = 0xFFFFFFu;
    private const uint Black = 0x000000u;

    /// <summary>
    /// Every field optional and every field required, the same discipline
    /// wintty_theme_test.zig's parser uses and for the same reason: a theme
    /// that drops a line must fail to parse rather than leave a colour at
    /// whatever the default was and pass by not testing anything.
    /// </summary>
    private sealed class ParsedTheme
    {
        public uint? Background;
        public uint? Foreground;
        public uint? Cursor;
        public uint? SelectionBackground;
        public uint? SelectionForeground;
        public readonly uint[] Palette = new uint[16];
        public readonly bool[] PaletteSeen = new bool[16];

        public uint Bg => Background ?? throw new InvalidOperationException("no background");
        public uint Fg => Foreground ?? throw new InvalidOperationException("no foreground");
    }

    private static string ThemeSource()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ThemeResource)
            ?? throw new InvalidOperationException(
                $"missing embedded resource {ThemeResource}; is the EmbeddedResource entry still in Ghostty.Tests.csproj?");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Lift one <c>pub const &lt;half&gt;: []const u8 = \\...;</c> block out of
    /// the zig source as plain config lines. Reading the source beats copying
    /// the literals: a copy is a thing somebody has to remember to update,
    /// and the whole point of this file is that nobody has to remember.
    /// </summary>
    private static string HalfSource(string half)
    {
        var lines = ThemeSource().Split('\n');
        var body = new List<string>();
        var inside = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (!inside)
            {
                if (line.StartsWith($"pub const {half}:", StringComparison.Ordinal)
                    || line.StartsWith($"pub const {half} :", StringComparison.Ordinal))
                {
                    inside = true;
                }
                continue;
            }
            var trimmed = line.Trim();
            if (trimmed == ";") break;
            if (!trimmed.StartsWith("\\\\", StringComparison.Ordinal)) continue;
            var value = trimmed[2..].Trim();
            if (value.Length > 0) body.Add(value);
        }
        Assert.True(body.Count > 0,
            $"found no body for the built-in '{half}' half; the shape of wintty_theme.zig changed");
        return string.Join('\n', body);
    }

    private static uint ParseHex(string s)
    {
        var body = s.StartsWith('#') ? s[1..] : s;
        if (body.Length != 6) throw new FormatException($"bad hex '{s}'");
        return Convert.ToUInt32(body, 16);
    }

    private static ParsedTheme Parse(string source)
    {
        var theme = new ParsedTheme();
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) throw new FormatException($"no '=' in theme line '{line}'");
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "background": theme.Background = ParseHex(value); break;
                case "foreground": theme.Foreground = ParseHex(value); break;
                case "cursor-color": theme.Cursor = ParseHex(value); break;
                case "selection-background": theme.SelectionBackground = ParseHex(value); break;
                case "selection-foreground": theme.SelectionForeground = ParseHex(value); break;
                case "palette":
                {
                    var inner = value.IndexOf('=');
                    if (inner < 0) throw new FormatException($"no palette index in '{line}'");
                    var index = int.Parse(value[..inner]);
                    if (index is < 0 or > 15) throw new FormatException($"palette index {index} out of range");
                    theme.Palette[index] = ParseHex(value[(inner + 1)..]);
                    theme.PaletteSeen[index] = true;
                    break;
                }
                // An unrecognised key is an error rather than a silent skip,
                // so a typo fails here instead of leaving a colour unset.
                default: throw new FormatException($"unknown theme key '{key}'");
            }
        }
        if (theme.Background is null) throw new FormatException("theme has no background");
        if (theme.Foreground is null) throw new FormatException("theme has no foreground");
        if (theme.Cursor is null) throw new FormatException("theme has no cursor-color");
        if (theme.SelectionBackground is null) throw new FormatException("theme has no selection-background");
        if (theme.SelectionForeground is null) throw new FormatException("theme has no selection-foreground");
        for (var i = 0; i < 16; i++)
            if (!theme.PaletteSeen[i]) throw new FormatException($"theme has no palette slot {i}");
        return theme;
    }

    private static ParsedTheme Half(string name) => Parse(HalfSource(name));

    /// <summary>
    /// What a reader actually sees for an unselected row title: the pole the
    /// strip picks, composited at its own alpha onto the ground, scored
    /// against that ground. Never the pole against the ground.
    /// </summary>
    private static double InactiveInkRatio(uint ground)
    {
        var pole = ThemeResolution.PreferLightForegroundAtAlpha(ground, InactiveInkAlpha)
            ? White
            : Black;
        var composited = ThemeResolution.CompositeOver(pole, InactiveInkAlpha, ground);
        return ThemeResolution.ContrastRatio(composited, ground);
    }

    public static TheoryData<string> Halves => new() { "light", "dark" };

    [Theory]
    [MemberData(nameof(Halves))]
    public void BothHalves_ParseCompletely(string half)
    {
        var theme = Half(half);
        Assert.NotNull(theme.Background);
        Assert.NotNull(theme.Foreground);
        Assert.NotNull(theme.Cursor);
        Assert.NotNull(theme.SelectionBackground);
        Assert.NotNull(theme.SelectionForeground);
        Assert.All(theme.PaletteSeen, seen => Assert.True(seen));
    }

    /// <summary>
    /// The built-in pair must be legible on its own terms, without the
    /// readability floor having to rescue it. Asserting
    /// EnsureReadableForeground's OUTPUT clears 4.5 would be vacuous -- it
    /// returns the better of black and white, which is never below about
    /// 4.58 for any background. What can fail, and what matters, is that the
    /// theme's own foreground already clears the floor, so the strip paints
    /// the palette's colour rather than a rescue pole.
    /// </summary>
    [Theory]
    [MemberData(nameof(Halves))]
    public void SelectedRowInk_IsThePalettesOwnForeground_NotARescuePole(string half)
    {
        var theme = Half(half);
        var ratio = ThemeResolution.ContrastRatio(theme.Bg, theme.Fg);
        Assert.True(ratio >= WcagAaText,
            $"the built-in '{half}' foreground is {ratio:N2}:1 on its own background, under the {WcagAaText} floor");
        Assert.Equal(theme.Fg, ThemeResolution.EnsureReadableForeground(theme.Bg, theme.Fg));
    }

    /// <summary>
    /// Unselected strip titles at 70% alpha over the theme's own background.
    /// This is the shape that measured 2.37:1 once, and it is not vacuous:
    /// muting to 70% pulls both candidate poles towards the ground, and for a
    /// mid-luminance ground the better of the two still lands under 4.5 (see
    /// the known-bad ground below).
    /// </summary>
    [Theory]
    [MemberData(nameof(Halves))]
    public void InactiveRowInk_ClearsAA_OnTheThemesOwnGround(string half)
    {
        var theme = Half(half);
        var ratio = InactiveInkRatio(theme.Bg);
        Assert.True(ratio >= WcagAaText,
            $"muted ink on the built-in '{half}' background is {ratio:N2}:1, under the {WcagAaText} floor");
    }

    // ---- anti-vacuity: the rules above have to be able to fail ----------

    /// <summary>
    /// A mid-grey ground defeats the muted-ink rule: at 70% alpha the better
    /// pole is still under AA. If this ever starts passing, the rule above is
    /// no longer measuring anything and both are worthless.
    /// </summary>
    [Fact]
    public void InactiveInkRule_FailsOnAKnownBadGround()
    {
        var ratio = InactiveInkRatio(0x808080u);
        Assert.True(ratio < WcagAaText,
            $"the muted-ink rule scored a mid-grey ground at {ratio:N2}:1, which clears AA; the rule has stopped having teeth");
        Assert.True(ratio > 1.0);
    }

    /// <summary>
    /// A dropped line has to fail parsing. Left unset, a colour would be
    /// whatever the field defaulted to, and a zeroed background clears every
    /// ratio below it -- so the theme would pass by not being tested.
    /// </summary>
    [Fact]
    public void ParseRule_RefusesAThemeWithADroppedLine()
    {
        var complete = HalfSource("dark");
        var dropped = string.Join('\n',
            complete.Split('\n').Where(l => !l.StartsWith("foreground", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => Parse(dropped));

        var droppedSlot = string.Join('\n',
            complete.Split('\n').Where(l => !l.StartsWith("palette = 7=", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => Parse(droppedSlot));
    }

    /// <summary>
    /// And an unknown key has to fail too, so a typo does not silently leave
    /// the colour it meant to set at its default.
    /// </summary>
    [Fact]
    public void ParseRule_RefusesAnUnknownKey()
    {
        var source = HalfSource("light") + "\nbackgruond = #000000";
        Assert.Throws<FormatException>(() => Parse(source));
    }
}
