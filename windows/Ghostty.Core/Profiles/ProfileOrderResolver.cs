using System;
using System.Collections.Generic;
using System.Linq;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Composes user-defined + discovered profiles into the final visible
/// list. Order: profile-order entries first (in their listed order),
/// then unlisted user profiles in the order they were defined, then
/// unlisted discovered profiles in alphabetical-by-ID order. Hidden
/// profiles (either via ProfileDef.Hidden or the hidden set parameter)
/// are omitted entirely.
/// </summary>
public static class ProfileOrderResolver
{
    public static IReadOnlyList<ResolvedProfile> Resolve(
        IReadOnlyList<ProfileDef> user,
        IReadOnlyList<DiscoveredProfile> discovered,
        IReadOnlyList<string>? profileOrder,
        string? defaultProfileId,
        IReadOnlySet<string> hidden)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(hidden);

        var combined = new Dictionary<string, ProfileDef>();
        var userOrder = new List<string>();
        foreach (var u in user)
        {
            combined[u.Id] = u;
            userOrder.Add(u.Id);
        }
        var discoveredById = new Dictionary<string, DiscoveredProfile>();
        foreach (var d in discovered)
        {
            discoveredById[d.Id] = d;
            if (!combined.ContainsKey(d.Id))
            {
                combined[d.Id] = new ProfileDef(
                    Id: d.Id,
                    Name: d.Name,
                    Command: d.Command,
                    WorkingDirectory: d.WorkingDirectory,
                    Icon: d.Icon,
                    TabTitle: d.TabTitle,
                    Hidden: false,
                    ProbeId: d.ProbeId,
                    VisualsOrNull: null);
            }
        }

        var ordered = new List<string>();
        var seen = new HashSet<string>();

        if (profileOrder is not null)
        {
            foreach (var id in profileOrder)
            {
                if (!combined.ContainsKey(id)) continue;
                if (seen.Add(id)) ordered.Add(id);
            }
        }

        foreach (var id in userOrder)
            if (seen.Add(id)) ordered.Add(id);

        foreach (var id in discoveredById.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (seen.Add(id)) ordered.Add(id);

        var result = new List<ResolvedProfile>();
        var index = 0;
        var defaultResolved = ResolveDefault(defaultProfileId, ordered, combined, hidden);
        foreach (var id in ordered)
        {
            var def = combined[id];
            if (def.Hidden || hidden.Contains(id)) continue;
            result.Add(new ResolvedProfile(
                Id: def.Id,
                Name: def.Name,
                Command: def.Command,
                WorkingDirectory: def.WorkingDirectory,
                Icon: def.Icon ?? new IconSpec.BundledKey("default"),
                TabTitle: def.TabTitle ?? def.Name,
                Visuals: def.Visuals,
                ProbeId: def.ProbeId,
                OrderIndex: index++,
                IsDefault: def.Id == defaultResolved));
        }
        return result;
    }

    private static string? ResolveDefault(
        string? requested,
        List<string> ordered,
        Dictionary<string, ProfileDef> combined,
        IReadOnlySet<string> hidden)
    {
        if (requested is not null
            && combined.TryGetValue(requested, out var def)
            && !def.Hidden
            && !hidden.Contains(requested))
        {
            return requested;
        }
        foreach (var id in ordered)
        {
            var d = combined[id];
            if (!d.Hidden && !hidden.Contains(id)) return id;
        }
        return null;
    }
}
