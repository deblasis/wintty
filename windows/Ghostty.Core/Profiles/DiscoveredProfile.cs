namespace Ghostty.Core.Profiles;

/// <summary>
/// One result from an <see cref="IInstalledShellProbe"/>. Discovered
/// profiles always supply a Command (probes guarantee non-empty).
/// </summary>
public sealed record DiscoveredProfile(
    string Id,
    string Name,
    string Command,
    string ProbeId,
    string? WorkingDirectory = null,
    IconSpec? Icon = null,
    string? TabTitle = null);
