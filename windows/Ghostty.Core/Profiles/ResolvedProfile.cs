namespace Ghostty.Core.Profiles;

/// <summary>
/// A profile after merge of user-defined + discovered + ordering +
/// hidden filtering. This is what UI consumers see. ProbeId is set
/// when this profile originated from discovery.
/// </summary>
public sealed record ResolvedProfile(
    string Id,
    string Name,
    string Command,
    string? WorkingDirectory,
    IconSpec Icon,
    string TabTitle,
    EffectiveVisualOverrides Visuals,
    string? ProbeId,
    int OrderIndex,
    bool IsDefault);
