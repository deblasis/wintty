using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ghostty.Core.Ssh;

/// <summary>
/// Builds the <c>scp</c> argument vectors for transfers to and from an
/// <see cref="SshTarget"/>. Pure, so the exact arguments are pinned by
/// unit tests.
///
/// <para>
/// Always <c>-s</c>: the SFTP protocol, where a remote path is data. The
/// legacy scp protocol hands the path to the remote shell, and the paths
/// here come from terminal output a remote program controls -- a file
/// named <c>x;reboot</c> must stay a file name. An scp too old to know
/// <c>-s</c> fails instead of falling back.
/// </para>
/// </summary>
public static class ScpCommand
{
    /// <summary>
    /// Upload <paramref name="localPaths"/> into <paramref name="remoteDirectory"/>.
    /// <paramref name="batch"/> forbids prompts (for a hidden run); a
    /// visible run leaves it off so scp can ask for a password.
    /// </summary>
    public static IReadOnlyList<string> Upload(
        SshTarget target,
        IReadOnlyList<string> localPaths,
        string remoteDirectory,
        bool recursive,
        bool batch)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(localPaths);
        if (localPaths.Count == 0) throw new ArgumentException("no files to upload", nameof(localPaths));
        if (!RemotePath.IsValid(remoteDirectory)) throw new ArgumentException("invalid remote directory", nameof(remoteDirectory));

        var args = Common(target, recursive, batch);
        args.Add("--");
        args.AddRange(localPaths);

        // The trailing slash makes scp require an existing directory
        // rather than quietly writing a single file under that name.
        var dir = RemotePath.ForSftp(remoteDirectory);
        if (!dir.EndsWith('/')) dir += "/";
        args.Add(target.RemotePrefix + dir);
        return args;
    }

    /// <summary>
    /// Download <paramref name="remotePath"/> (a file or a directory) into
    /// the existing local directory <paramref name="localDirectory"/>.
    /// </summary>
    public static IReadOnlyList<string> Download(
        SshTarget target,
        string remotePath,
        string localDirectory,
        bool batch)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(localDirectory);
        if (!RemotePath.IsValid(remotePath)) throw new ArgumentException("invalid remote path", nameof(remotePath));

        // -r so a directory works; it is harmless for a file.
        var args = Common(target, recursive: true, batch);
        args.Add("--");
        args.Add(target.RemotePrefix + RemotePath.EscapeGlob(RemotePath.ForSftp(remotePath)));
        args.Add(localDirectory);
        return args;
    }

    private static List<string> Common(SshTarget target, bool recursive, bool batch)
    {
        var args = new List<string> { "-s" };
        if (batch) args.Add("-B");
        if (recursive) args.Add("-r");
        if (target.Port is { } port)
        {
            args.Add("-P");
            args.Add(port.ToString(CultureInfo.InvariantCulture));
        }
        args.AddRange(target.PassThrough);
        return args;
    }

    /// <summary>
    /// Join an argv into one Windows command line that
    /// <see cref="SshCommandLine.SplitWindows"/> (and the C runtime) splits
    /// back into the same arguments.
    /// </summary>
    public static string ToCommandLine(string program, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendQuoted(sb, program);
        foreach (var arg in args)
        {
            sb.Append(' ');
            AppendQuoted(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
            }
            backslashes = 0;
        }
        // Backslashes before the closing quote must be doubled.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }
}

/// <summary>Why a hidden (no-prompt) scp run did not succeed.</summary>
public enum ScpFailureKind
{
    /// <summary>
    /// The connection needs something only the user can give: a password,
    /// a key passphrase, or a yes to an unknown host key. Running the same
    /// transfer where they can see and answer it will work.
    /// </summary>
    NeedsInteraction,

    /// <summary>Anything else: no such file, permission on the path, network.</summary>
    Other,
}

public static class ScpFailure
{
    private static readonly string[] InteractionMarkers =
    [
        "Permission denied (",
        "Host key verification failed",
        "authenticity of host",
        "Too many authentication failures",
        "passphrase",
        "password",
        "No more authentication methods",
        "keyboard-interactive",
    ];

    public static ScpFailureKind Classify(string? stderr)
    {
        if (!string.IsNullOrEmpty(stderr))
        {
            foreach (var marker in InteractionMarkers)
                if (stderr.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return ScpFailureKind.NeedsInteraction;
        }
        return ScpFailureKind.Other;
    }

    /// <summary>
    /// The lines of scp's stderr worth showing: the last few non-empty
    /// ones, without the debug chatter.
    /// </summary>
    public static string Summarize(string? stderr, int maxLines = 3)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "scp failed without an error message.";
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var start = Math.Max(0, lines.Length - maxLines);
        return string.Join("\n", lines, start, lines.Length - start);
    }
}
