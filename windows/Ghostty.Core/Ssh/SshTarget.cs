using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ghostty.Core.Ssh;

/// <summary>
/// Where a pane's ssh session goes, recovered from the command line of
/// the <c>ssh.exe</c> running in it. Carries exactly what a second
/// connection (an scp transfer) needs to reach the same place the same
/// way: the destination as the user spelled it (so an alias from
/// <c>~/.ssh/config</c> keeps working), the login and port, and the
/// connection options that matter to authentication and routing.
/// </summary>
public sealed record SshTarget(
    string Destination,
    string? User = null,
    int? Port = null,
    IReadOnlyList<string>? Options = null)
{
    /// <summary>
    /// Extra scp arguments (<c>-i file</c>, <c>-J jump</c>, <c>-F config</c>,
    /// <c>-o Key=Value</c>, <c>-4</c>/<c>-6</c>/<c>-C</c>), already split.
    /// </summary>
    public IReadOnlyList<string> PassThrough => Options ?? Array.Empty<string>();

    /// <summary>"user@host" or "host", for display.</summary>
    public string Display => User is null ? Destination : $"{User}@{Destination}";

    /// <summary>
    /// The <c>[user@]host:</c> prefix of an scp remote operand. A literal
    /// IPv6 address is bracketed so its colons are not read as the
    /// host/path separator.
    /// </summary>
    public string RemotePrefix
    {
        get
        {
            var host = Destination.Contains(':') && Destination[0] != '['
                ? "[" + Destination + "]"
                : Destination;
            return (User is null ? host : User + "@" + host) + ":";
        }
    }
}

/// <summary>
/// Parses an <c>ssh</c> command line into an <see cref="SshTarget"/>.
/// Pure, so it is unit-testable without a process to inspect.
/// </summary>
public static class SshCommandLine
{
    // ssh(1) getopt string, OpenSSH 9.x: options that take an argument...
    private const string WithArgument = "BbcDEeFIiJLlmOoPpQRSWw";
    // ...and the plain flags. Anything else is not an ssh we understand.
    private const string Flags = "46AaCfGgKkMNnqsTtVvXxYy";

    // -o keys that describe this one session, not how to reach the host.
    // A transfer must not inherit them: a forward would fail to bind a
    // second time and RemoteCommand would replace the sftp subsystem.
    private static readonly HashSet<string> SessionOnlyOptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "RemoteCommand", "RequestTTY", "LocalForward", "RemoteForward",
            "DynamicForward", "SessionType", "StdinNull", "ForkAfterAuthentication",
            "LocalCommand", "PermitLocalCommand", "ControlMaster", "ControlPath",
            "ControlPersist", "ExitOnForwardFailure", "Tunnel", "TunnelDevice",
            "ForwardX11", "ForwardX11Trusted", "ForwardAgent", "ClearAllForwardings",
            "EscapeChar", "LogLevel", "BatchMode",
        };

    /// <summary>
    /// Parse a raw Windows command line (as read from the process). Null
    /// when it is not an ssh invocation or names no destination.
    /// </summary>
    public static SshTarget? Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        return Parse(SplitWindows(commandLine));
    }

    /// <summary>Parse an already-split argv; <c>argv[0]</c> is the program.</summary>
    public static SshTarget? Parse(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2 || !IsSshProgram(argv[0])) return null;

        string? user = null;
        int? port = null;
        var options = new List<string>();
        string? destination = null;

        for (var i = 1; i < argv.Count; i++)
        {
            var arg = argv[i];
            if (arg == "--")
            {
                if (i + 1 < argv.Count) destination = argv[i + 1];
                break;
            }
            if (arg.Length < 2 || arg[0] != '-')
            {
                destination = arg;
                break;
            }

            // A cluster of short options: "-vp22", "-4A", "-p 22".
            for (var j = 1; j < arg.Length; j++)
            {
                var c = arg[j];
                if (Flags.Contains(c))
                {
                    if (c is '4' or '6' or 'C') options.Add("-" + c);
                    continue;
                }
                if (!WithArgument.Contains(c)) return null;

                string value;
                if (j + 1 < arg.Length) value = arg[(j + 1)..];
                else if (i + 1 < argv.Count) value = argv[++i];
                else return null;

                switch (c)
                {
                    case 'p':
                        if (!TryParsePort(value, out var p)) return null;
                        port = p;
                        break;
                    case 'l':
                        user = value;
                        break;
                    case 'i' or 'J' or 'F':
                        options.Add("-" + c);
                        options.Add(value);
                        break;
                    case 'o':
                        ApplyOption(value, options, ref user, ref port);
                        break;
                }
                break;
            }
        }

        if (string.IsNullOrEmpty(destination)) return null;
        if (!TrySplitDestination(destination, ref user, ref port, out var host)) return null;
        return new SshTarget(host, user, port, options);
    }

    private static void ApplyOption(string value, List<string> options, ref string? user, ref int? port)
    {
        // "Key=Value" or "Key Value".
        var sep = value.IndexOfAny(['=', ' ']);
        var key = (sep < 0 ? value : value[..sep]).Trim();
        var val = sep < 0 ? "" : value[(sep + 1)..].Trim();
        if (key.Length == 0 || SessionOnlyOptions.Contains(key)) return;

        if (key.Equals("User", StringComparison.OrdinalIgnoreCase))
        {
            if (val.Length > 0) user ??= val;
            return;
        }
        if (key.Equals("Port", StringComparison.OrdinalIgnoreCase))
        {
            if (port is null && TryParsePort(val, out var p)) port = p;
            return;
        }
        options.Add("-o");
        options.Add(value);
    }

    // "host", "user@host", or "ssh://[user@]host[:port]". An explicit -l
    // or -p on the command line wins over what the destination carries,
    // as it does in ssh itself.
    private static bool TrySplitDestination(string destination, ref string? user, ref int? port, out string host)
    {
        host = destination;
        const string Scheme = "ssh://";
        var isUri = destination.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);
        if (isUri) host = destination[Scheme.Length..].TrimEnd('/');

        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            if (at > 0) user ??= host[..at];
            host = host[(at + 1)..];
        }

        if (isUri)
        {
            // [v6]:port or host:port.
            if (host.StartsWith('['))
            {
                var close = host.IndexOf(']');
                if (close < 0) return false;
                var rest = host[(close + 1)..];
                host = host[1..close];
                if (rest.StartsWith(':') && TryParsePort(rest[1..], out var p6)) port ??= p6;
            }
            else
            {
                var colon = host.LastIndexOf(':');
                if (colon >= 0)
                {
                    if (TryParsePort(host[(colon + 1)..], out var p)) port ??= p;
                    host = host[..colon];
                }
            }
        }

        return host.Length > 0 && host[0] != '-' && IsPlainText(host) && (user is null || IsPlainText(user));
    }

    private static bool IsPlainText(string s)
    {
        foreach (var c in s)
            if (c <= ' ' || c == 0x7f) return false;
        return s.Length > 0;
    }

    private static bool TryParsePort(string s, out int port)
        => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out port)
           && port is >= 1 and <= 65535;

    private static bool IsSshProgram(string program)
    {
        var slash = program.LastIndexOfAny(['\\', '/']);
        var name = slash < 0 ? program : program[(slash + 1)..];
        return name.Equals("ssh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Split a Windows command line the way <c>CommandLineToArgvW</c> and
    /// the C runtime do: whitespace separates, double quotes group, and
    /// backslashes are literal except in front of a quote, where 2n
    /// backslashes mean n and 2n+1 mean n plus a literal quote.
    /// </summary>
    public static IReadOnlyList<string> SplitWindows(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var run = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { run++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1) { current.Append('"'); i++; }
                }
                else
                {
                    current.Append('\\', run);
                }
                hasToken = true;
                continue;
            }
            if (c == '"')
            {
                // "" inside a quoted run is one literal quote.
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i += 2;
                    continue;
                }
                inQuotes = !inQuotes;
                hasToken = true;
                i++;
                continue;
            }
            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                i++;
                continue;
            }
            current.Append(c);
            hasToken = true;
            i++;
        }
        if (hasToken) args.Add(current.ToString());
        return args;
    }
}
