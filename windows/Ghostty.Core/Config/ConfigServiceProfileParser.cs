using System;
using System.Collections.Generic;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.Config;

/// <summary>
/// Pure helper invoked by <c>ConfigService.ReadFlagsCore</c> after the
/// raw file cache is populated. Returns the five profile-view values
/// exposed on <see cref="IConfigService"/> and
/// <see cref="IProfileConfigSource"/>. Kept separate from
/// <c>ConfigService</c> so the parse logic is unit-testable on Linux
/// without the WinUI + libghostty dependency chain.
/// </summary>
public static class ConfigServiceProfileParser
{
    /// <summary>
    /// <paramref name="configText"/> is the raw file contents. The
    /// <paramref name="fileValueReader"/> delegate is a thin adapter
    /// over <c>ConfigService</c>'s existing <c>GetFileValue</c>
    /// helper: it returns the last raw value in force for a key -- config
    /// keys are last-wins, and a blank <c>key = </c> is the reset, so a
    /// reset key reads as absent -- or <see langword="null"/> when the key
    /// is absent. Has no production caller since <c>ConfigService.ReadFlagsCore</c>
    /// reads the ini cache; kept as the reference implementation the pairs
    /// overload is tested against and as the simpler fixture API for tests.
    /// </summary>
    public static ProfileView ParseAll(
        string configText,
        Func<string, string?> fileValueReader)
    {
        ArgumentNullException.ThrowIfNull(configText);
        ArgumentNullException.ThrowIfNull(fileValueReader);

        var parsed = ProfileSourceParser.Parse(configText);
        var hidden = ProfileSourceParser.ExtractHiddenIds(configText);
        var hiddenMentions = ProfileSourceParser.ExtractHiddenMentionIds(configText);

        return BuildView(parsed, hidden, hiddenMentions, fileValueReader);
    }

    /// <summary>
    /// Same result as <see cref="ParseAll(string, Func{string, string?})"/>,
    /// built from the config-file cache <c>ConfigService.ReadFlags</c>
    /// already populated (<see cref="ConfigIniFile.Load"/>'s shape), instead
    /// of a second raw-text read of the same file. Callers on the hot path
    /// (<c>ConfigService.ReadFlagsCore</c>) should prefer this overload.
    /// </summary>
    public static ProfileView ParseAll(
        IReadOnlyDictionary<string, List<string>> configPairs,
        Func<string, string?> fileValueReader)
    {
        ArgumentNullException.ThrowIfNull(configPairs);
        ArgumentNullException.ThrowIfNull(fileValueReader);

        var parsed = ProfileSourceParser.Parse(configPairs);
        var hidden = ProfileSourceParser.ExtractHiddenIds(configPairs);
        var hiddenMentions = ProfileSourceParser.ExtractHiddenMentionIds(configPairs);

        var view = BuildView(parsed, hidden, hiddenMentions, fileValueReader);

        // Saved ssh connections (ssh.<id>.* blocks). Their warnings join
        // the profile warnings: the settings UI shows one list.
        var ssh = Ghostty.Core.Ssh.SshConnectionParser.Parse(configPairs);
        if (ssh.Connections.Count == 0 && ssh.Warnings.Count == 0) return view;
        return view with
        {
            SshConnections = ssh.Connections,
            ProfileWarnings = ssh.Warnings.Count == 0
                ? view.ProfileWarnings
                : [.. view.ProfileWarnings, .. ssh.Warnings],
        };
    }

    private static ProfileView BuildView(
        ProfileParseResult parsed,
        IReadOnlySet<string> hidden,
        IReadOnlySet<string> hiddenMentions,
        Func<string, string?> fileValueReader)
    {
        var defaultId = fileValueReader("default-profile");
        if (string.IsNullOrEmpty(defaultId)) defaultId = null;

        var profileOrderRaw = fileValueReader("profile-order") ?? string.Empty;
        var profileOrder = ParseCsv(profileOrderRaw);

        // Suppress warnings for ids that appear only as a hidden-override
        // (e.g. "profile.foo.hidden = true" or "= false" with no
        // name/command). Both directions are intentional suppression
        // markers, not malformed definitions; the un-hide path of the
        // settings-page toggle writes hidden = false, so filtering only
        // on the true-set would leak false-positive warnings.
        var warnings = SuppressExplainedWarnings(
            parsed.Warnings, parsed.Profiles, hiddenMentions);

        // ssh-hosts-discovery is opt-in: only an explicit true enables it.
        var sshHostsDiscovery = bool.TryParse(fileValueReader("ssh-hosts-discovery"), out var sshOn) && sshOn;
        var sshHostsUser = fileValueReader("ssh-hosts-user");
        if (string.IsNullOrWhiteSpace(sshHostsUser)) sshHostsUser = null;

        return new ProfileView(
            ParsedProfiles: parsed.Profiles,
            ProfileOverrides: parsed.Overrides,
            ProfileOrder: profileOrder,
            DefaultProfileId: defaultId,
            HiddenProfileIds: hidden,
            ProfileWarnings: warnings,
            SshHostsDiscovery: sshHostsDiscovery,
            SshHostsUser: sshHostsUser?.Trim());
    }

    /// <summary>
    /// Drop the warnings belonging to <paramref name="explainedIds"/>, the
    /// ids whose <c>profile.&lt;id&gt;.*</c> block is an intentional
    /// partial override rather than a malformed definition. Used twice,
    /// once per pass that can explain one:
    /// <list type="bullet">
    /// <item>here, for the hidden-only markers (<see cref="ProfileSourceParser.ExtractHiddenMentionIds"/>);</item>
    /// <item>in <c>ProfileRegistry</c>, for the ids a partial block resolved
    /// onto once discovery answered -- discovery is not visible from here,
    /// which is why the second pass has to happen at the merge.</item>
    /// </list>
    /// An anchor on the exact "profile '&lt;id&gt;':" prefix
    /// <see cref="ProfileSourceParser"/> emits, so one id that happens to be
    /// a substring of another id's warning does not suppress it. Ids that
    /// also have a full parsed definition are skipped: a warning about a
    /// block that does define a profile is for a genuinely broken block,
    /// not an override marker.
    /// </summary>
    public static IReadOnlyList<string> SuppressExplainedWarnings(
        IReadOnlyList<string> warnings,
        IReadOnlyDictionary<string, ProfileDef> profiles,
        IReadOnlySet<string> explainedIds)
    {
        if (warnings.Count == 0) return warnings;

        // Precompute the "profile '<id>':" prefixes once per explained id
        // rather than per (warning x id) pair; a string interpolation
        // inside the inner loop would allocate a new format string on every
        // iteration.
        List<string>? prefixes = null;
        foreach (var id in explainedIds)
        {
            if (profiles.ContainsKey(id)) continue;
            (prefixes ??= new List<string>(explainedIds.Count)).Add($"profile '{id}':");
        }
        if (prefixes is null) return warnings;

        var result = new List<string>(warnings.Count);
        foreach (var w in warnings)
        {
            var suppressed = false;
            foreach (var prefix in prefixes)
            {
                if (w.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    suppressed = true;
                    break;
                }
            }
            if (!suppressed) result.Add(w);
        }
        return result;
    }

    private static IReadOnlyList<string> ParseCsv(string input)
    {
        if (input.Length == 0) return Array.Empty<string>();
        // TrimEntries + RemoveEmptyEntries collapses the pre-existing
        // trim-then-skip-empties loop into the BCL split options.
        return input.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>
/// Immutable bundle of the profile-view values. Matches the
/// member shape of <see cref="IProfileConfigSource"/>.
/// </summary>
public sealed record ProfileView(
    IReadOnlyDictionary<string, ProfileDef> ParsedProfiles,
    IReadOnlyDictionary<string, ProfileOverride> ProfileOverrides,
    IReadOnlyList<string> ProfileOrder,
    string? DefaultProfileId,
    IReadOnlySet<string> HiddenProfileIds,
    IReadOnlyList<string> ProfileWarnings,
    bool SshHostsDiscovery = false,
    string? SshHostsUser = null)
{
    /// <summary>Saved connections from <c>ssh.&lt;id&gt;.*</c> blocks.</summary>
    public IReadOnlyList<Ghostty.Core.Ssh.SshConnection> SshConnections { get; init; }
        = Array.Empty<Ghostty.Core.Ssh.SshConnection>();
}
