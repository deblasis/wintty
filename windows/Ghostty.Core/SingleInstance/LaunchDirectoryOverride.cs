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
/// The dialect mirrors the CLI the cold start runs in every cell that decides
/// a directory: values are trimmed of ASCII whitespace the way
/// <c>parseCLI</c> trims them, one surrounding pair of double quotes is
/// stripped the way it strips them, a repeated flag applies its LAST value
/// the way the config cascade applies argv in order, everything after the
/// first <c>-e</c> is command payload and never a flag (mirroring
/// <see cref="LaunchCommand"/>'s boundary), and <c>~</c> expands only before
/// a forward slash, which is the one shape libghostty's Windows home
/// expansion accepts. <c>home</c> reads <c>USERPROFILE</c>, then
/// <c>HOMEDRIVE</c>+<c>HOMEPATH</c>, the same order the native side reads
/// them. A relative value resolves against the directory the launch was
/// started in — the wire's working-directory field is exactly that base —
/// anchored by Win32 normalization rather than an existence-checked realpath:
/// trailing dots fold and links keep their spelling, and the receiver's own
/// directory check remains the arbiter of what exists.
///
/// A resolved value is NOT passed through the spawn policy, because on the
/// cold leg this flag is a CONFIG source and a user-configured directory is
/// the class that policy exempts (its scope note says only reported values
/// pass through it, and the native side documents a working directory naming
/// a UNC share as legitimate). Gating the forwarded half alone would make
/// the same flag work or silently fall back depending on whether a primary
/// happened to be running. A uniform spawn-directory policy across every
/// leg is a separate question.
///
/// Two cells deliberately diverge from the cold parser, both in the safe
/// direction: an occurrence whose value trims or strips to empty contributes
/// nothing here — for the trim-empty spelling the cold parser diagnoses it
/// and leaves any earlier value standing, which this preserves; the quoted
/// spelling the cold parser records and the receiver drops, and this treats
/// as no value a beat earlier — and a value that cannot resolve against its
/// base at all is no override rather than an error.
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
    /// trims or strips to nothing is no value.
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

            // parseCLI strips one surrounding pair of double quotes after
            // trimming; a value that kept them would fail the receiver's
            // directory check and silently fall back. A value that is empty
            // once stripped (a launcher quoting a possibly-empty variable, or
            // only whitespace inside the quotes) contributes nothing:
            // GetFullPath would otherwise fold it into the base and turn a
            // no-op into the launch's own directory.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            if (TrimAscii(value).Length == 0) continue;

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
    /// resolves relatives on the client, against the caller's cwd). A null,
    /// empty or whitespace-only <paramref name="requested"/> is null: callers
    /// apply their inherited rules, never a guess — a whitespace-only value
    /// would otherwise fold into the base directory.
    /// </summary>
    public static string? Resolve(string? requested, string inheritedDirectory)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;

        if (string.Equals(requested, "home", StringComparison.Ordinal))
            return HomeDirectory();

        if (string.Equals(requested, "inherit", StringComparison.Ordinal))
            return inheritedDirectory;

        // libghostty's Windows home expansion accepts `~/` only; `~\` is a
        // literal relative path there, and one dialect is what this is for.
        if (requested.StartsWith("~/", StringComparison.Ordinal))
            return HomeDirectory() + "\\" + requested[2..];

        if (Path.IsPathFullyQualified(requested))
            return requested;

        try
        {
            return Path.GetFullPath(requested, inheritedDirectory);
        }
        catch (Exception)
        {
            // A value that cannot resolve to a path is no override; the
            // receiver would drop it with a warning anyway.
            return null;
        }
    }

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
