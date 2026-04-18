namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Visual tuple consumed by <c>UpdatePillViewModel</c>. Produced by the
/// pure <see cref="PillDisplayModel"/> function from an
/// <see cref="UpdateStateSnapshot"/>.
/// </summary>
/// <param name="IsVisible">Whether the pill is shown at all.</param>
/// <param name="Label">Localized text to show (e.g. "Update Available: 1.4.2").</param>
/// <param name="IconGlyph">Segoe Fluent Icons glyph (single code point as string).</param>
/// <param name="ThemeBrushKey">
/// ResourceDictionary key resolved by the pill XAML for background brush.
/// Theme dictionaries supply Light/Dark/HighContrast variants.
/// </param>
/// <param name="ShowProgressRing">Whether to overlay a ProgressRing on the icon.</param>
/// <param name="ProgressValue">Progress 0..1 when <see cref="ShowProgressRing"/> is true and <see cref="IsIndeterminate"/> is false.</param>
/// <param name="IsIndeterminate">True for states without a meaningful progress value (Extracting, Installing). The ring spins instead of filling.</param>
public readonly record struct PillDisplay(
    bool IsVisible,
    string Label,
    string IconGlyph,
    string ThemeBrushKey,
    bool ShowProgressRing,
    double ProgressValue,
    bool IsIndeterminate = false);
