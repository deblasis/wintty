using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Turns the user's OpenSSH <c>known_hosts</c> into new-tab profiles, one
/// per host (opt-in via <c>ssh-hosts-discovery</c>). Pure: the caller reads
/// the file, so this is unit-testable without a filesystem.
///
/// Not a discovery probe on purpose: probe results are cached for 24h, and
/// these depend on config (the toggle and <c>ssh-hosts-user</c>), so they
/// are rebuilt on every config reload instead.
///
/// What is skipped, and why:
///   - hashed lines (<c>|1|salt|hash</c>, HashKnownHosts): the name is gone;
///   - marker lines (<c>@cert-authority</c>, <c>@revoked</c>): not hosts;
///   - negated or wildcard patterns (<c>!host</c>, <c>*.example.com</c>);
///   - lines whose names are all IP addresses: a list of IPs is noise, and
///     a host reached by name is listed under that name;
///   - names outside <c>[A-Za-z0-9._-]</c>: the command is split and spawned
///     directly, so nothing unusual reaches it.
/// Aliases on one line (<c>devel.local,192.168.0.44</c>) are one host; a
/// host seen on several lines (one per key type) is listed once.
/// </summary>
public static class SshKnownHosts
{
    /// <summary>Probe id stamped on the results, and the id prefix.</summary>
    public const string ProbeId = "ssh";

    public static IReadOnlyList<DiscoveredProfile> Parse(string? knownHostsText, string? user)
    {
        if (string.IsNullOrEmpty(knownHostsText)) return Array.Empty<DiscoveredProfile>();

        var login = user is { Length: > 0 } u && IsSafeToken(u) ? u : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hosts = new List<(string Host, int? Port)>();

        foreach (var rawLine in knownHostsText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or '@' or '|') continue;

            var end = line.IndexOfAny([' ', '\t']);
            var field = end < 0 ? line : line[..end];

            (string Host, int? Port)? pick = null;
            foreach (var pattern in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParsePattern(pattern, out var host, out var port)) continue;
                if (IsIpAddress(host)) continue;
                pick = (host, port);
                break;
            }
            if (pick is not { } p) continue;

            var key = p.Port is { } k ? $"{p.Host}:{k}" : p.Host;
            if (seen.Add(key)) hosts.Add(p);
        }

        hosts.Sort((a, b) =>
        {
            var byHost = string.Compare(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);
            return byHost != 0 ? byHost : Nullable.Compare(a.Port, b.Port);
        });

        var profiles = new List<DiscoveredProfile>(hosts.Count);
        foreach (var (host, port) in hosts)
        {
            var target = login is null ? host : $"{login}@{host}";
            profiles.Add(new DiscoveredProfile(
                Id: ProbeId + "-" + Slug(host) + (port is { } n ? "-" + n.ToString(CultureInfo.InvariantCulture) : ""),
                Name: port is { } q ? $"SSH: {host}:{q}" : $"SSH: {host}",
                Command: port is { } r ? $"ssh -p {r} {target}" : $"ssh {target}",
                ProbeId: ProbeId,
                Icon: new IconSpec.BrandKey("ssh", null)));
        }
        return profiles;
    }

    // "host" or "[host]:port". A bracketed entry without a port, or with
    // port 22, is the plain host.
    private static bool TryParsePattern(string pattern, out string host, out int? port)
    {
        host = pattern;
        port = null;
        if (pattern.Length == 0 || pattern[0] == '!' || pattern.Contains('*') || pattern.Contains('?'))
            return false;

        if (pattern[0] == '[')
        {
            var close = pattern.IndexOf(']');
            if (close <= 1) return false;
            host = pattern[1..close];
            var rest = pattern[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':' ||
                    !int.TryParse(rest.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ||
                    n is < 1 or > 65535)
                    return false;
                if (n != 22) port = n;
            }
        }
        return IsSafeToken(host);
    }

    // IPv4 dotted quads and anything with a colon (IPv6; a bracketed
    // host has already had its brackets and port removed).
    private static bool IsIpAddress(string host)
    {
        if (host.Contains(':')) return true;
        foreach (var c in host)
            if (c is not ((>= '0' and <= '9') or '.')) return false;
        return true;
    }

    private static bool IsSafeToken(string s)
    {
        if (s.Length == 0 || s[0] == '-') return false;
        foreach (var c in s)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_'))
                return false;
        }
        return true;
    }

    // Lowercase, keep [a-z0-9], everything else becomes one '-', so the
    // id matches the profile id format ([a-z0-9-]+).
    private static string Slug(string host)
    {
        var sb = new StringBuilder(host.Length);
        foreach (var c in host.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
