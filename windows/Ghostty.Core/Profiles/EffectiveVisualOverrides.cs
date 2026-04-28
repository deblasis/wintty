namespace Ghostty.Core.Profiles;

/// <summary>
/// The five per-profile visual override keys.
/// All fields are optional; null means "inherit from base config".
/// </summary>
public sealed record EffectiveVisualOverrides(
    string? Theme = null,
    double? BackgroundOpacity = null,
    string? FontFamily = null,
    double? FontSize = null,
    string? CursorStyle = null)
{
    public static readonly EffectiveVisualOverrides Empty = new();
}
