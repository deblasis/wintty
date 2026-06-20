namespace Ghostty.Core.Accessibility;

/// <summary>
/// The four Windows system colors that define the High Contrast surface
/// palette. Each value is a raw Win32 COLORREF (0x00BBGGRR) as returned by
/// GetSysColor; conversion to Ghostty's #rrggbb config form happens in
/// <see cref="HighContrastConfigWriter"/>.
/// </summary>
public readonly record struct HighContrastColors(
    uint Background,
    uint Foreground,
    uint SelectionBackground,
    uint SelectionForeground);
