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
/// are returned in <see cref="ResolvedProfileSet.Hidden"/> rather than
/// the visible list, so the settings-UI inspector can offer an unhide
/// affordance without re-running the resolver against a different
/// hidden set.
/// </summary>
public static class ProfileOrderResolver
{
    public static ResolvedProfileSet Resolve(
        IReadOnlyList<ProfileDef> user,
        IReadOnlyList<DiscoveredProfile> discovered,
        IReadOnlyList<string>? profileOrder,
        string? defaultProfileId,
        IReadOnlySet<string> hiddenIds)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(hiddenIds);

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

        var visible = new List<ResolvedProfile>();
        var hidden = new List<ResolvedProfile>();
        var visibleIndex = 0;
        var hiddenIndex = 0;
        var defaultResolved = ResolveDefault(defaultProfileId, ordered, combined, hiddenIds);
        foreach (var id in ordered)
        {
            var def = combined[id];
            var isHidden = def.Hidden || hiddenIds.Contains(id);
            if (isHidden)
                hidden.Add(MakeProfile(def, hiddenIndex++, isDefault: false));
            else
                visible.Add(MakeProfile(def, visibleIndex++, isDefault: def.Id == defaultResolved));
        }
        return new ResolvedProfileSet(visible, hidden);
    }

    // Single source of truth for the ProfileDef -> ResolvedProfile
    // projection so the visible / hidden branches above stay in sync as
    // the record gains fields.
    private static ResolvedProfile MakeProfile(ProfileDef def, int orderIndex, bool isDefault)
        => new(
            Id: def.Id,
            Name: def.Name,
            Command: def.Command,
            WorkingDirectory: def.WorkingDirectory,
            Icon: ResolveFallbackIcon(def),
            TabTitle: def.TabTitle ?? def.Name,
            Visuals: def.Visuals,
            ProbeId: def.ProbeId,
            OrderIndex: orderIndex,
            IsDefault: isDefault,
            TabIconTracksForeground: def.TabIconTracksForeground);

    // When a profile has no explicit icon, derive one from the command's
    // exe basename via ProcessIconTable so user-defined profiles like
    // `command = pwsh.exe` pick up the pwsh brand glyph instead of the
    // generic wintty default.
    private static IconSpec ResolveFallbackIcon(ProfileDef def)
    {
        if (def.Icon is not null) return def.Icon;

        var basename = TryGetCommandBasename(def.Command);
        if (basename is not null)
        {
            var mapped = ProcessIconTable.TryMap(basename, def.Command);
            if (mapped is not null) return mapped;
        }
        return new IconSpec.BundledKey("default");
    }

    // Hoisted so each call doesn't allocate a fresh char[] for IndexOfAny.
    private static readonly char[] s_whitespace = [' ', '\t'];

    private static string? TryGetCommandBasename(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        // Strip leading whitespace + outer quotes; take the first token.
        // Tokens after the first are intentionally ignored: the basename
        // of argv[0] is what runs as the foreground process at exec time.
        var trimmed = command.TrimStart();
        string first;
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote < 0) return null;
            first = trimmed.Substring(1, endQuote - 1);
        }
        else
        {
            var space = trimmed.IndexOfAny(s_whitespace);
            first = space < 0 ? trimmed : trimmed.Substring(0, space);
        }
        if (first.Length == 0) return null;
        // Path.GetFileName handles both forward and backward slashes.
        var name = System.IO.Path.GetFileName(first);
        if (string.IsNullOrEmpty(name)) return null;
        // ProcessIconTable keys end in .exe; treat a bare `command = pwsh`
        // (no extension) as if the user wrote `pwsh.exe`. This is the
        // PATH-resolution case where the shell would append .exe anyway.
        if (!name.Contains('.')) name += ".exe";
        return name;
    }

    private static string? ResolveDefault(
        string? requested,
        List<string> ordered,
        Dictionary<string, ProfileDef> combined,
        IReadOnlySet<string> hiddenIds)
    {
        if (requested is not null
            && combined.TryGetValue(requested, out var def)
            && !def.Hidden
            && !hiddenIds.Contains(requested))
        {
            return requested;
        }
        foreach (var id in ordered)
        {
            var d = combined[id];
            if (!d.Hidden && !hiddenIds.Contains(id)) return id;
        }
        return null;
    }
}
