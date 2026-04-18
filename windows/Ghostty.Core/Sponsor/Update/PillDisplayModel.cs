using System.Globalization;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Pure function mapping <see cref="UpdateStateSnapshot"/> to the
/// <see cref="PillDisplay"/> consumed by the WinUI 3 pill. Exhaustive
/// over every <see cref="UpdateState"/> value so no state can produce
/// the wrong visuals silently.
///
/// Segoe Fluent Icons glyph codepoints chosen to match the WinUI 3
/// Fluent System Icons guidance:
///  - Idle / NoUpdatesFound: checkmark     U+E73E
///  - UpdateAvailable: arrow-down-circle   U+EB9D
///  - Downloading: no glyph (ProgressRing covers icon slot)
///  - Extracting:  box-with-arrow          U+F158
///  - Installing:  gear                    U+E713
///  - RestartPending: refresh              U+E72C
///  - Error: warning triangle              U+E7BA
/// </summary>
public static class PillDisplayModel
{
    public const string GlyphCheck = "\uE73E";
    public const string GlyphArrowDownCircle = "\uEB9D";
    public const string GlyphBoxArrow = "\uF158";
    public const string GlyphGear = "\uE713";
    public const string GlyphRefresh = "\uE72C";
    public const string GlyphWarning = "\uE7BA";

    public const string BrushNeutral = "SubtleFillColorSecondaryBrush";
    public const string BrushAccent = "SystemAccentColorBrush";
    public const string BrushCaution = "SystemFillColorCautionBrush";

    public static PillDisplay MapFromState(UpdateStateSnapshot s) => s.State switch
    {
        UpdateState.Idle => new PillDisplay(
            IsVisible: false,
            Label: string.Empty,
            IconGlyph: string.Empty,
            ThemeBrushKey: BrushNeutral,
            ShowProgressRing: false,
            ProgressValue: 0),

        UpdateState.NoUpdatesFound => new PillDisplay(
            IsVisible: true,
            Label: "No Updates Found",
            IconGlyph: GlyphCheck,
            ThemeBrushKey: BrushNeutral,
            ShowProgressRing: false,
            ProgressValue: 0),

        UpdateState.UpdateAvailable => new PillDisplay(
            IsVisible: true,
            Label: $"Update Available: {s.TargetVersion ?? "?"}",
            IconGlyph: GlyphArrowDownCircle,
            ThemeBrushKey: BrushAccent,
            ShowProgressRing: false,
            ProgressValue: 0),

        UpdateState.Downloading => new PillDisplay(
            IsVisible: true,
            Label: $"Downloading... {ToPercent(s.Progress)}%",
            IconGlyph: string.Empty,
            ThemeBrushKey: BrushNeutral,
            ShowProgressRing: true,
            ProgressValue: s.Progress ?? 0),

        UpdateState.Extracting => new PillDisplay(
            IsVisible: true,
            Label: "Preparing update...",
            IconGlyph: GlyphBoxArrow,
            ThemeBrushKey: BrushNeutral,
            ShowProgressRing: true,
            ProgressValue: 0,
            IsIndeterminate: true),

        UpdateState.Installing => new PillDisplay(
            IsVisible: true,
            Label: "Installing update...",
            IconGlyph: GlyphGear,
            ThemeBrushKey: BrushNeutral,
            ShowProgressRing: true,
            ProgressValue: 0,
            IsIndeterminate: true),

        UpdateState.RestartPending => new PillDisplay(
            IsVisible: true,
            Label: "Restart to Complete Update",
            IconGlyph: GlyphRefresh,
            ThemeBrushKey: BrushAccent,
            ShowProgressRing: false,
            ProgressValue: 0),

        UpdateState.Error => new PillDisplay(
            IsVisible: true,
            Label: "Update Error",
            IconGlyph: GlyphWarning,
            ThemeBrushKey: BrushCaution,
            ShowProgressRing: false,
            ProgressValue: 0),

        _ => new PillDisplay(false, string.Empty, string.Empty, BrushNeutral, false, 0),
    };

    private static int ToPercent(double? progress) =>
        progress is null
            ? 0
            : (int)System.Math.Clamp(System.Math.Round(progress.Value * 100), 0, 100);
}
