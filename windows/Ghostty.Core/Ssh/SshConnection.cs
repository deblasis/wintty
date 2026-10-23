using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.Ssh;

/// <summary>
/// One saved ssh connection, from an <c>ssh.&lt;id&gt;.*</c> block in the
/// config file:
/// <code>
/// ssh.devel.host = devel.local
/// ssh.devel.user = jgill
/// ssh.devel.port = 2222
/// ssh.devel.identity-file = ~/.ssh/id_ed25519
/// ssh.devel.jump-host = bastion.example.com
/// ssh.devel.forward-agent = true
/// ssh.devel.name = Devel box
/// </code>
/// Only <c>host</c> is required. Each connection becomes a new-tab
/// profile with the id <c>ssh-&lt;id&gt;</c>.
/// </summary>
public sealed record SshConnection(
    string Id,
    string Host,
    string? Name = null,
    string? User = null,
    int? Port = null,
    string? IdentityFile = null,
    string? JumpHost = null,
    bool ForwardAgent = false)
{
    /// <summary>Prefix of the profile id a connection is listed under.</summary>
    public const string ProfileIdPrefix = "ssh-";

    public string ProfileId => ProfileIdPrefix + Id;

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? "SSH: " + (User is null ? Host : $"{User}@{Host}") + (Port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : "")
        : Name!;

    /// <summary>
    /// The command the profile runs. Every part was validated when the
    /// connection was parsed, so nothing here needs shell escaping beyond
    /// the quotes around the identity file.
    /// </summary>
    public string Command(string? userProfileDirectory = null)
    {
        var sb = new StringBuilder("ssh");
        if (Port is { } port) sb.Append(" -p ").Append(port.ToString(CultureInfo.InvariantCulture));
        if (IdentityFile is { Length: > 0 } identity)
            sb.Append(" -i \"").Append(ExpandHome(identity, userProfileDirectory)).Append('"');
        if (JumpHost is { Length: > 0 } jump) sb.Append(" -J ").Append(jump);
        if (ForwardAgent) sb.Append(" -A");
        sb.Append(' ').Append(User is null ? Host : $"{User}@{Host}");
        return sb.ToString();
    }

    public DiscoveredProfile ToProfile(string? userProfileDirectory = null) => new(
        Id: ProfileId,
        Name: DisplayName,
        Command: Command(userProfileDirectory),
        ProbeId: SshKnownHosts.ProbeId,
        Icon: new IconSpec.BrandKey("ssh", null));

    private static string ExpandHome(string path, string? home)
    {
        if (string.IsNullOrEmpty(home)) return path;
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return home.TrimEnd('\\', '/') + "\\" + path[2..].Replace('/', '\\');
        return path;
    }
}

public sealed record SshConnectionParseResult(
    IReadOnlyList<SshConnection> Connections,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads and writes <c>ssh.&lt;id&gt;.*</c> config keys. Pure.
///
/// Values are validated on the way in because they end up on a command
/// line: host, user and jump host are limited to the characters real
/// names use, and an identity file may not contain the characters
/// <c>cmd.exe</c> would interpret inside quotes.
/// </summary>
public static partial class SshConnectionParser
{
    public const string KeyPrefix = "ssh.";

    public static readonly IReadOnlyList<string> SubKeys =
        ["name", "host", "user", "port", "identity-file", "jump-host", "forward-agent"];

    [GeneratedRegex(@"^ssh\.([a-z0-9-]+)\.([a-z0-9-]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex IdRegex();

    /// <summary>True for a key of the shape <c>ssh.&lt;id&gt;.&lt;subkey&gt;</c>.</summary>
    public static bool IsConnectionKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return KeyRegex().IsMatch(key);
    }

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 48 && IdRegex().IsMatch(id);

    /// <summary>
    /// Parse the config-file cache (<c>ConfigIniFile.Load</c>'s shape: key
    /// to every value in file order; the last one wins).
    /// </summary>
    public static SshConnectionParseResult Parse(IReadOnlyDictionary<string, List<string>> configPairs)
    {
        ArgumentNullException.ThrowIfNull(configPairs);

        var groups = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (rawKey, values) in configPairs)
        {
            if (values.Count == 0) continue;
            var match = KeyRegex().Match(rawKey);
            if (!match.Success) continue;
            var id = match.Groups[1].Value.ToLowerInvariant();
            var subKey = match.Groups[2].Value.ToLowerInvariant();
            if (!groups.TryGetValue(id, out var bag))
                groups[id] = bag = new Dictionary<string, string>(StringComparer.Ordinal);
            bag[subKey] = values[^1].Trim();
        }

        var connections = new List<SshConnection>(groups.Count);
        var warnings = new List<string>();
        foreach (var (id, bag) in groups)
        {
            var error = TryBuild(id, bag, out var connection);
            if (error is null) connections.Add(connection!);
            else warnings.Add($"ssh '{id}': {error}, dropped");
        }
        return new SshConnectionParseResult(connections, warnings);
    }

    /// <summary>
    /// Validate one connection's fields. Returns null and the connection
    /// when it is usable, otherwise the reason it is not. Shared by the
    /// config parser and the settings editor so both apply one rule.
    /// </summary>
    public static string? TryBuild(string id, IReadOnlyDictionary<string, string> fields, out SshConnection? connection)
    {
        connection = null;
        if (!IsValidId(id)) return "the id must be lowercase letters, digits and dashes";

        var host = Get(fields, "host");
        if (host is null) return "missing required key 'host'";
        if (!IsHostToken(host)) return "'host' has characters a host name cannot contain";

        var user = Get(fields, "user");
        if (user is not null && !IsUserToken(user)) return "'user' has characters a login cannot contain";

        int? port = null;
        if (Get(fields, "port") is { } portText)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
                return "'port' must be a number from 1 to 65535";
            if (p != 22) port = p;
        }

        var identity = Get(fields, "identity-file");
        if (identity is not null && !IsPathToken(identity))
            return "'identity-file' has characters that are not allowed in the path";

        var jump = Get(fields, "jump-host");
        if (jump is not null && !IsJumpToken(jump)) return "'jump-host' must look like [user@]host[:port]";

        var forwardAgent = false;
        if (Get(fields, "forward-agent") is { } fa && !bool.TryParse(fa, out forwardAgent))
            return "'forward-agent' must be true or false";

        var name = Get(fields, "name");
        if (name is not null && !IsDisplayText(name)) return "'name' has control characters";

        connection = new SshConnection(id, host, name, user, port, identity, jump, forwardAgent);
        return null;
    }

    /// <summary>
    /// The key/value lines that define <paramref name="connection"/>, in
    /// <see cref="SubKeys"/> order. Unset optional fields are omitted.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ToConfig(SshConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var prefix = KeyPrefix + connection.Id + ".";
        var lines = new List<KeyValuePair<string, string>>();
        void Add(string subKey, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.Add(new(prefix + subKey, value.Trim()));
        }
        Add("name", connection.Name);
        Add("host", connection.Host);
        Add("user", connection.User);
        Add("port", connection.Port?.ToString(CultureInfo.InvariantCulture));
        Add("identity-file", connection.IdentityFile);
        Add("jump-host", connection.JumpHost);
        Add("forward-agent", connection.ForwardAgent ? "true" : null);
        return lines;
    }

    /// <summary>
    /// Suggest an id for a new connection from its host: lowercase, with
    /// runs of other characters collapsed to one dash, made unique against
    /// <paramref name="taken"/>.
    /// </summary>
    public static string SuggestId(string host, IReadOnlyCollection<string> taken)
    {
        var sb = new StringBuilder();
        foreach (var c in (host ?? "").ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var stem = sb.ToString().Trim('-');
        if (stem.Length == 0) stem = "host";
        if (stem.Length > 40) stem = stem[..40].Trim('-');

        var id = stem;
        for (var n = 2; Contains(taken, id); n++)
            id = stem + "-" + n.ToString(CultureInfo.InvariantCulture);
        return id;
    }

    private static bool Contains(IReadOnlyCollection<string> taken, string id)
    {
        foreach (var t in taken)
            if (string.Equals(t, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? Get(IReadOnlyDictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    // Host names, IPv4, and IPv6 literals (colons). Never a leading dash:
    // that would be read as an option.
    private static bool IsHostToken(string s)
    {
        if (s.Length is 0 or > 253 || s[0] == '-') return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':')) return false;
        return true;
    }

    private static bool IsUserToken(string s)
    {
        if (s.Length is 0 or > 64 || s[0] == '-') return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '$')) return false;
        return true;
    }

    // [user@]host[:port], or several separated by commas (ssh -J a,b).
    private static bool IsJumpToken(string s)
    {
        if (s.Length is 0 or > 512 || s[0] == '-') return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '@' or ',' or '[' or ']')) return false;
        return true;
    }

    // The path is placed inside double quotes on a command line. Inside
    // quotes cmd.exe still expands %VAR% and (with delayed expansion)
    // !VAR!, and a quote would end the quoting.
    private static bool IsPathToken(string s)
    {
        if (s.Length is 0 or > 260 || s[0] == '-') return false;
        foreach (var c in s)
            if (c < ' ' || c == 0x7f || c is '"' or '%' or '!' or '^' or '<' or '>' or '|' or '&' or '*' or '?') return false;
        return !s.EndsWith('\\');
    }

    private static bool IsDisplayText(string s)
    {
        if (s.Length > 80) return false;
        foreach (var c in s)
            if (char.IsControl(c)) return false;
        return true;
    }
}

/// <summary>
/// Lists the concrete <c>Host</c> aliases in an OpenSSH client config
/// (<c>~/.ssh/config</c>) as profiles that run <c>ssh &lt;alias&gt;</c>,
/// leaving every other setting to ssh itself. Wildcard and negated
/// patterns are not hosts and are skipped; <c>Match</c> blocks and
/// <c>Include</c> files are not followed.
/// </summary>
public static class SshConfigHosts
{
    public static IReadOnlyList<DiscoveredProfile> Parse(string? configText)
    {
        if (string.IsNullOrEmpty(configText)) return Array.Empty<DiscoveredProfile>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();
        foreach (var rawLine in configText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // "Host a b" or "Host=a b".
            var sep = line.IndexOfAny([' ', '\t', '=']);
            if (sep < 0 || !line.AsSpan(0, sep).Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;

            // Only the keyword separator may be '='; after it the patterns
            // are whitespace-separated.
            var patterns = line[(sep + 1)..].TrimStart(' ', '\t', '=');
            foreach (var raw in patterns.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                var alias = raw.Trim('"');
                if (!IsAlias(alias)) continue;
                if (seen.Add(alias)) aliases.Add(alias);
            }
        }

        aliases.Sort(StringComparer.OrdinalIgnoreCase);
        var profiles = new List<DiscoveredProfile>(aliases.Count);
        foreach (var alias in aliases)
        {
            var slug = SshConnectionParser.SuggestId(alias, Array.Empty<string>());
            profiles.Add(new DiscoveredProfile(
                Id: SshConnection.ProfileIdPrefix + slug,
                Name: "SSH: " + alias,
                Command: "ssh " + alias,
                ProbeId: SshKnownHosts.ProbeId,
                Icon: new IconSpec.BrandKey("ssh", null)));
        }
        return profiles;
    }

    private static bool IsAlias(string s)
    {
        if (s.Length is 0 or > 253 || s[0] is '-' or '!') return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return false;
        return true;
    }
}
