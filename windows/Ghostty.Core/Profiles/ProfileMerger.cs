using System;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Merges base-config visuals with a profile's overrides. The base
/// parameter must already be the effective base (i.e. the base config's
/// theme cascade has been resolved by the caller). Profile keys override
/// base keys when non-null; null profile keys inherit base.
/// </summary>
public static class ProfileMerger
{
    public static EffectiveVisualOverrides Merge(
        EffectiveVisualOverrides baseVisuals,
        EffectiveVisualOverrides profileOverrides)
    {
        ArgumentNullException.ThrowIfNull(baseVisuals);
        ArgumentNullException.ThrowIfNull(profileOverrides);

        return new EffectiveVisualOverrides(
            Theme: profileOverrides.Theme ?? baseVisuals.Theme,
            BackgroundOpacity: profileOverrides.BackgroundOpacity ?? baseVisuals.BackgroundOpacity,
            FontFamily: profileOverrides.FontFamily ?? baseVisuals.FontFamily,
            FontSize: profileOverrides.FontSize ?? baseVisuals.FontSize,
            CursorStyle: profileOverrides.CursorStyle ?? baseVisuals.CursorStyle);
    }
}
