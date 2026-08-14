using System;
using System.Collections.Generic;
using System.IO;

namespace Ghostty.Core.Shell;

/// <summary>
/// Decides which image the splash takes its texture from: the user's own if
/// they have supplied one, otherwise the sheet that shipped. Pure, so the
/// order and the naming are testable without touching a disk.
/// </summary>
/// <remarks>
/// <para>Presence is the whole signal. A file in the user's own directory
/// means they want it, and no file there means they do not, so nothing has
/// to work out whether the shipped sheet has been tampered with. The two
/// live in different places on purpose: an upgrade replaces what it
/// installed and never sees the user's copy, so a customisation survives
/// updates without either side knowing about the other.</para>
///
/// <para>The directories mirror <c>ThemeSearchPath</c>, which is where a
/// user already keeps the things they override -- config, themes -- so a
/// texture goes beside them rather than somewhere new.</para>
/// </remarks>
public static class LaunchTextureSource
{
    /// <summary>
    /// What a user names their own texture. Lower case with a hyphen,
    /// matching the config and theme files that sit beside it rather than
    /// the shipped asset's own name.
    /// </summary>
    public const string UserFileName = "splash-texture.png";

    /// <summary>The shipped sheet, relative to the application directory.</summary>
    public const string ShippedFileName = "Splash-Texture.png";

    /// <summary>
    /// Application directory names under the roaming root, current first.
    /// Both are read for the same reason the theme search reads both: an
    /// install can hold its files under either name.
    /// </summary>
    private static readonly string[] AppDirectoryNames = ["wintty", "ghostty"];

    /// <summary>Where a texture may be, in the order it is preferred.</summary>
    public readonly record struct Candidate(string Path, bool IsUserSupplied);

    public static IEnumerable<Candidate> Candidates(string? appData, string? baseDirectory)
    {
        if (!string.IsNullOrEmpty(appData))
        {
            foreach (var app in AppDirectoryNames)
            {
                yield return new Candidate(
                    Path.Combine(appData, app, UserFileName), IsUserSupplied: true);
            }
        }

        if (!string.IsNullOrEmpty(baseDirectory))
        {
            yield return new Candidate(
                Path.Combine(baseDirectory, "Assets", ShippedFileName), IsUserSupplied: false);
        }
    }

    /// <summary>
    /// The first candidate that exists, or null when there is no texture to
    /// draw at all -- which is not a fault, just a plainer splash.
    /// </summary>
    /// <param name="exists">
    /// Injected so the order can be tested without a filesystem.
    /// </param>
    public static Candidate? Resolve(
        string? appData, string? baseDirectory, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        foreach (var candidate in Candidates(appData, baseDirectory))
        {
            if (exists(candidate.Path)) return candidate;
        }

        return null;
    }
}
