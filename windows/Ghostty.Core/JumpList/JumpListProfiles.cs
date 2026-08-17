using System;
using System.Collections.Generic;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.JumpList;

/// <summary>
/// Maps the live <see cref="IProfileRegistry"/> snapshot onto jump-list
/// entries. Icon paths are only forwarded when the spec is a real file
/// the Shell can rasterise; font/bundled keys stay null and use the
/// app icon.
/// </summary>
internal static class JumpListProfiles
{
    public static IReadOnlyList<ProfileEntry> From(IReadOnlyList<ResolvedProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0) return Array.Empty<ProfileEntry>();

        var entries = new ProfileEntry[profiles.Count];
        for (var i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            entries[i] = new ProfileEntry(
                Id: p.Id,
                DisplayName: p.Name,
                IconPath: IconPath(p.Icon),
                ShellCommand: p.Command,
                WorkingDirectory: p.WorkingDirectory);
        }
        return entries;
    }

    private static string? IconPath(IconSpec icon) => icon switch
    {
        IconSpec.Path { FilePath: var path } => path,
        IconSpec.AutoForExe { ExePath: var exe } => exe,
        _ => null,
    };
}
