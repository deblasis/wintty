using System;

namespace Ghostty.Core.Theming;

/// <summary>
/// Resolves the chrome accent for the wintty shell from three
/// sources: an explicit <c>accent-color</c> config key, the terminal
/// <c>cursor-color</c>, and a palette-derived fallback. Precedence is
/// accent-color > cursor-color > palette: the explicit key always
/// wins when set, and cursor-color remains the implicit fallback so
/// users who only configure cursor-color keep the "cursor matches
/// chrome" look the shell-theme integration originally shipped with.
///
/// The palette fallback is a thunk so the (relatively expensive)
/// saturation scan in <c>ShellThemeService.FindAccent</c> is skipped
/// whenever either color is set.
/// </summary>
internal static class AccentColorResolver
{
    public static uint Resolve(uint? accentColor, uint? cursorColor, Func<uint> paletteFallback)
        => accentColor ?? cursorColor ?? paletteFallback();
}
