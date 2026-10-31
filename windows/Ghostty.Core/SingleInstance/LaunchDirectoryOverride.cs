using System;
using System.Collections.Generic;
using System.IO;

namespace Ghostty.Core.SingleInstance;

/// <summary>
/// The working directory a launch ASKED for on its own command line:
/// <c>--working-directory=&lt;dir&gt;</c>, the equals form libghostty's CLI
/// accepts (its parser takes no space-separated config values, so neither does
/// this). Unlike the process cwd a forwarded launch carries in the wire's
/// working-directory field — an inherited accident the launch policy rightly
/// drops for command-less launches — this is an explicit choice, which a cold
/// start honors through its own config. The primary of a forwarded launch
/// cannot borrow that config (it is the secondary's, and it died with the
/// secondary), so it reads the flag out of the argv the request already
/// carries and applies it to the pane it opens for that launch.
///
/// The dialect mirrors the CLI the cold start runs, cell for cell:
/// <see cref="Ghostty.Core.Session.SpawnCwdPolicy"/> permitting, values are
/// trimmed of ASCII whitespace the way <c>parseCLI</c> trims them, one
/// surrounding pair of double quotes is stripped the way it strips them,
/// a repeated flag applies its LAST value the way the config cascade applies
/// argv in order, everything after the first <c>-e</c> is command payload and
/// never a flag (mirroring <see cref="LaunchCommand"/>'s boundary), and
/// <c>~</c> expands only before a forward slash, which is the one shape
/// libghostty's Windows home expansion accepts. <c>home</c> reads
/// <c>USERPROFILE</c>, then <c>HOMEDRIVE</c>+<c>HOMEPATH</c>, the same order
/// the native side reads them. A relative value resolves against the
/// directory the launch was started in — the CLI resolves relatives on the
/// CLIENT, against the caller's cwd, before any receiver sees them, and the
/// wire's working-directory field is exactly that base.
///
/// A value that does not pass the spawn policy is no override: a remote UNC
/// directory names who would receive the user's credentials when the pane's
/// shell is spawned there, and a launch flag is not exempt from the rule every
/// other non-config cwd source passes.
///
/// Pure (no I/O beyond the profile path) so the model is unit-testable
/// without a GUI.
/// </summary>
public static class LaunchDirectoryOverride
{
    /// <summary>
    /// The flag, spelled exactly as libghostty's parser matches it: equals
    /// form only.
    /// </summary>
    public const string Prefix = "--working-directory=";

    /// <summary>
    /// The directory <paramref name="args"/> ask for, or null when they ask
    /// for none. The first element is the program and is never a flag (the
    /// CLI's iterator starts after it); the scan stops at the first
    /// <see cref="LaunchCommand.ExecuteFlag"/>, because everything after it
    /// is the command, not flags; within what remains, the LAST occurrence
    /// wins, the way the config cascade applies argv in order; a value that
    /// trims to nothing is no value.
    /// </summary>
    public static string? FromArgs(IReadOnlyList<string>? args)
    {
        if (args is null) return null;

        string? found = null;
        // Index 0 is the program. A program path can spell anything,
        // including this flag, and the CLI never reads it as one.
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is null) continue;
            if (string.Equals(arg, LaunchCommand.ExecuteFlag, StringComparison.Ordinal))
                break;

            if (!arg.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            var value = TrimAscii(arg[Prefix.Length..]);
            if (value.Length == 0) continue;

            // parseCLI strips one surrounding pair of double quotes after
            // trimming; a value that kept them would fail the receiver's
            // directory check and silently fall back.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            found = value;
        }

        return found;
    }

    /// <summary>
    /// The effective directory a launch asked for: <c>home</c> and a leading
    /// <c>~/</c> resolve against the user's profile the way the CLI resolves
    /// them, <c>inherit</c> falls back to the directory the launch was
    /// started in, anything else is taken as given when anchored and resolved
    /// against <paramref name="inheritedDirectory"/> when not (the CLI
    /// resolves relatives on the client, against the caller's cwd). A null
    /// <paramref name="requested"/> is null: callers apply their inherited
    /// rules, never a guess. A value the spawn policy refuses is null for the
    /// same reason, except <c>inherit</c>, which only names what the wire
    /// field already carries.
    /// </summary>
    public static string? Resolve(string? requested, string inheritedDirectory)
    {
        if (requested is null) return null;

        if (string.Equals(requested, "home", StringComparison.Ordinal))
            return PassesSpawnPolicy(HomeDirectory());

        if (string.Equals(requested, "inherit", StringComparison.Ordinal))
            return inheritedDirectory;

        // libghostty's Windows home expansion accepts `~/` only; `~\` is a
        // literal relative path there, and one dialect is what this is for.
        if (requested.StartsWith("~/", StringComparison.Ordinal))
            return PassesSpawnPolicy(HomeDirectory() + "\\" + requested[2..]);

        string resolved;
        if (Path.IsPathFullyQualified(requested))
        {
            resolved = requested;
        }
        else
        {
            try
            {
                resolved = Path.GetFullPath(requested, inheritedDirectory);
            }
            catch (Exception)
            {
                // A value that cannot resolve to a path is no override; the
                // receiver would drop it with a warning anyway.
                return null;
            }
        }

        return PassesSpawnPolicy(resolved);
    }

    /// <summary>
    /// The spawn policy's verdict, as an override value: refused means the
    /// launch's inherited rules apply.
    /// </summary>
    private static string? PassesSpawnPolicy(string directory)
        => Ghostty.Core.Session.SpawnCwdPolicy.MaySpawnAt(directory) ? directory : null;

    /// <summary>
    /// The user's home, read in the order the native side reads it:
    /// <c>USERPROFILE</c>, then <c>HOMEDRIVE</c>+<c>HOMEPATH</c>, then the
    /// known folder as the last resort.
    /// </summary>
    private static string HomeDirectory()
    {
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(profile)) return profile;

        var drive = Environment.GetEnvironmentVariable("HOMEDRIVE");
        var path = Environment.GetEnvironmentVariable("HOMEPATH");
        if (!string.IsNullOrEmpty(drive) && !string.IsNullOrEmpty(path))
            return drive + path;

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return folder;
    }

    /// <summary>
    /// The CLI trims ASCII whitespace only; the CLR's <c>Trim()</c> would
    /// also strip Unicode whitespace the native parser leaves in place.
    /// </summary>
    private static string TrimAscii(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsAsciiWhitespace(value[start])) start++;
        while (end > start && IsAsciiWhitespace(value[end - 1])) end--;
        return value[start..end];
    }

    private static bool IsAsciiWhitespace(char c)
        => c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';
}
