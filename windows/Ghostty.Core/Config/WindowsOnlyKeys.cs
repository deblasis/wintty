using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Ghostty.Core.Config;

/// <summary>
/// Registry of config keys the Windows fork introduces that are not
/// in upstream Ghostty's Zig config schema. libghostty flags these as
/// "unknown field" during parse, but we handle them ourselves by
/// reading the raw config file; this registry lets us suppress the
/// false-positive diagnostics and surface the keys as informational
/// instead.
/// </summary>
public static class WindowsOnlyKeys
{
    public readonly record struct Entry(string Key, string Description);

    public static readonly IReadOnlyList<Entry> All =
    [
        new("background-style",
            "Backdrop material preset (solid/frosted/crystal)."),
        new("background-tint-color",
            "Tint color overlaid on the acrylic backdrop."),
        new("background-tint-opacity",
            "Strength of the acrylic tint color."),
        new("background-luminosity-opacity",
            "Strength of the acrylic luminosity layer."),
        new("background-blur-follows-opacity",
            "Reduce blur radius as background-opacity increases."),
        new("background-gradient-point",
            "Position/color/radius of a radial gradient source (repeatable)."),
        new("background-gradient-animation",
            "Motion preset applied to gradient points."),
        new("background-gradient-speed",
            "Animation speed multiplier for gradient motion."),
        new("background-gradient-blend",
            "Whether the gradient renders over or under terminal text."),
        new("background-gradient-opacity",
            "Strength of the gradient tint layer."),
        new("accent-color",
            "Color of the active tab background, focus border, and tab strip rail. When unset, the chrome follows cursor-color."),
        new("vertical-tabs",
            "Tab strip orientation. When true, tabs render in a vertical sidebar instead of the default horizontal strip."),
        new("command-palette-group-commands",
            "Group entries in the command palette by category instead of listing them flat."),
        new("command-palette-background",
            "Backdrop material for the command palette (acrylic / mica / opaque)."),
        new("power-saver-mode",
            "How the app reacts to Windows power-saving signals (auto, always, never)."),
        new("default-profile",
            "Id of the profile opened for a new tab or window when none is specified."),
        new("profile-order",
            "Comma-separated list of profile ids defining the order shown in the tab picker and command palette."),
        new("no-color-override",
            "How Wintty reacts to a NO_COLOR value inherited from the environment: notify (default -- honor NO_COLOR but show a one-time notice offering to enable color), strip (enable color by removing NO_COLOR from spawned shells), or keep (honor NO_COLOR silently)."),
        new("windows-single-instance",
            "When true, a second launch is routed into the already-running instance (opens a new window) instead of starting a separate process."),
        new("windows-high-contrast",
            "When true (default), the terminal surface follows the Windows High Contrast theme automatically; set false to keep your configured colors even in High Contrast mode."),
    ];

    public static readonly FrozenSet<string> Set =
        All.Select(e => e.Key).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Case-insensitive lookup from key to its <see cref="Entry"/>, used
    /// by the settings UI to surface descriptions (e.g. as tooltips on
    /// the code pills in Raw Editor).
    /// </summary>
    public static readonly FrozenDictionary<string, Entry> ByKey =
        All.ToFrozenDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string key) => Set.Contains(key);

    /// <summary>
    /// Extract the config key from a libghostty "unknown field"
    /// diagnostic. The precomputed message format (emitted by
    /// <c>src/cli/diagnostics.zig</c>'s <c>Diagnostic.format</c>) is
    /// <c>[FILE:LINE:|cli:IDX:]KEY: unknown field</c> when the key is
    /// populated, or <c>[FILE:LINE:|cli:IDX:] unknown field</c> when
    /// the diagnostic has no key. Returns false if the message doesn't
    /// end in the suffix at all; returns true with whatever token sits
    /// before the suffix otherwise (benign if the token isn't in
    /// <see cref="Set"/>, since the caller treats non-matches as
    /// regular diagnostics).
    /// </summary>
    /// <remarks>
    /// Windows paths in the prefix contain colons (e.g. C:\Users\...),
    /// but config keys themselves never do, so splitting on the final
    /// ':' before the suffix gives the key unambiguously. If upstream
    /// ever changes the formatter (trailing punctuation, different
    /// separator), the <c>EndsWith</c> check will stop matching and
    /// every Windows-only diagnostic will surface as a regular error;
    /// tests in <c>WindowsOnlyKeysTests</c> pin the current shapes.
    /// </remarks>
    public static bool TryExtractUnknownFieldKey(string? message, out string key)
    {
        const string Suffix = ": unknown field";
        if (message is null || !message.EndsWith(Suffix, StringComparison.Ordinal))
        {
            key = string.Empty;
            return false;
        }
        var prefix = message[..^Suffix.Length];
        var lastColon = prefix.LastIndexOf(':');
        key = lastColon >= 0 ? prefix[(lastColon + 1)..] : prefix;
        return key.Length > 0;
    }

    /// <summary>
    /// Returns true when <paramref name="key"/> is a dotted
    /// per-profile key of the shape <c>profile.&lt;id&gt;.&lt;subkey&gt;</c>.
    /// Used by <c>ConfigService</c>'s diagnostic filter to absorb
    /// libghostty's "unknown field" output for user-defined profile
    /// blocks without polluting <c>WindowsOnlyKeysUsed</c> with one
    /// entry per subkey per profile.
    /// </summary>
    public static bool IsProfileSubkey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        const string Prefix = "profile.";
        if (!key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Must have at least one character of <id>, then '.', then at
        // least one character of <subkey>. IndexOf('.', Prefix.Length)
        // skips the initial "profile." dot and looks for the id-subkey
        // separator; the result must be strictly greater than
        // Prefix.Length (non-empty id) and strictly less than the
        // string end (non-empty subkey).
        var sep = key.IndexOf('.', Prefix.Length);
        return sep > Prefix.Length && sep < key.Length - 1;
    }

    /// <summary>
    /// Returns true when <paramref name="key"/> is an internal-namespace
    /// key of the shape <c>internal.&lt;name&gt;</c>. These are reserved
    /// for app-private knobs (e.g. update simulator toggles) that are
    /// read directly from the raw config file and are not meant to be
    /// surfaced as Windows-only public config; suppressing them here
    /// keeps libghostty's "unknown field" diagnostic from leaking into
    /// the settings UI notice list.
    /// </summary>
    public static bool IsInternalKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        const string Prefix = "internal.";
        // Need the full prefix plus at least one character of <name>;
        // bare "internal." or "internal" alone shouldn't match.
        return key.Length > Prefix.Length
            && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when <paramref name="key"/> is an agent-detector key
    /// of the shape <c>agent-detect.&lt;name&gt;</c>. These are a
    /// Windows-fork-only feature (custom tab-icon process detectors) read
    /// directly from the raw config file by the Pro tab-icon layer and are
    /// not surfaced as Windows-only public config; suppressing them here
    /// keeps libghostty's "unknown field" diagnostic out of the settings
    /// UI notice list, exactly like <see cref="IsInternalKey"/>. The
    /// matcher mirrors that reader's prefix test, so a name that itself
    /// contains dots (<c>agent-detect.my.bot</c>) still matches.
    /// </summary>
    public static bool IsAgentDetectKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        const string Prefix = "agent-detect.";
        // Need the full prefix plus at least one character of <name>;
        // bare "agent-detect." or "agent-detect" alone shouldn't match.
        return key.Length > Prefix.Length
            && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }
}
