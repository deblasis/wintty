namespace Ghostty.Core.Settings;

/// <summary>
/// HSV color used by the picker UI. H is in degrees [0, 360), S and V
/// are fractions in [0, 1]. Conversion lives on <see cref="Rgb"/> so
/// consumers can round-trip without reaching across types.
/// </summary>
public readonly record struct Hsv(double H, double S, double V);
