using System;
using System.Collections.Generic;

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
/// The special values the CLI accepts are resolved by
/// <see cref="Resolve"/>: <c>home</c>, <c>inherit</c>, and a leading
/// <c>~</c>. The first occurrence wins, matching the CLI's seen-once
/// handling of the flag.
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
    /// for none. Values are trimmed of surrounding whitespace the way the
    /// CLI trims them; an argument that is only the bare flag carries no
    /// value and no override (a cold start diagnoses it; a forward falls
    /// back to the inherited rules rather than guessing one).
    /// </summary>
    public static string? FromArgs(IReadOnlyList<string>? args)
    {
        if (args is null) return null;

        foreach (var arg in args)
        {
            if (arg is null || !arg.StartsWith(Prefix, StringComparison.Ordinal))
                continue;

            var value = arg[Prefix.Length..].Trim();
            if (value.Length == 0) continue;
            return value;
        }

        return null;
    }

    /// <summary>
    /// The effective directory a launch asked for: <c>home</c> and a
    /// leading <c>~</c> resolve against the user's profile, <c>inherit</c>
    /// falls back to the directory the launch was started in, and anything
    /// else is taken as given. A null <paramref name="requested"/> is null:
    /// callers apply their inherited rules, never a guess.
    /// </summary>
    public static string? Resolve(string? requested, string inheritedDirectory)
    {
        if (requested is null) return null;

        if (string.Equals(requested, "home", StringComparison.Ordinal))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.Equals(requested, "inherit", StringComparison.Ordinal))
            return inheritedDirectory;

        if (requested.StartsWith("~\\", StringComparison.Ordinal)
            || requested.StartsWith("~/", StringComparison.Ordinal))
        {
            // The profile path is a Windows path; the separator after ~ may
            // be either kind, and the pane gets a Windows one.
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                + "\\" + requested[2..];
        }

        return requested;
    }
}
