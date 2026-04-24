namespace Ghostty.Core.Profiles;

/// <summary>
/// Parsed-from-config representation of one profile. Immutable.
/// Discovered profiles use the same shape; the ProbeId field is set
/// for discovered, null for user-defined.
/// </summary>
public sealed record ProfileDef(
    string Id,
    string Name,
    string Command,
    string? WorkingDirectory = null,
    IconSpec? Icon = null,
    string? TabTitle = null,
    bool Hidden = false,
    string? ProbeId = null,
    EffectiveVisualOverrides? VisualsOrNull = null)
{
    /// <summary>
    /// Non-null view of <see cref="VisualsOrNull"/>; returns
    /// <see cref="EffectiveVisualOverrides.Empty"/> if the parser did
    /// not see any visual override keys for this profile.
    /// </summary>
    public EffectiveVisualOverrides Visuals => VisualsOrNull ?? EffectiveVisualOverrides.Empty;
}
