using System.Text;

namespace Ghostty.Core.Version;

/// <summary>
/// The one-line identity of a build, both versions each under its own
/// prefix: <c>Wintty w1.0.0-rc.1 (tip) on libghostty v1.3.2-dev</c>.
///
/// The libghostty half is upstream's <c>build.zig.zon</c> value, which
/// upstream bumps to <c>X.Y.Z-dev</c> right after each release, so between
/// releases it reads as "after vX.Y.(Z-1)". The Wintty half is the cut
/// version; this public tree hardcodes it to 0.0.0 (it has no release
/// path), and the release build stamps the real one in.
///
/// Display only. <see cref="BuildInfo.WinttyVersionString"/> keeps its
/// <c>&lt;version&gt;-&lt;cadence&gt;+&lt;commit&gt;</c> shape because the release
/// scripts read it back and assert on it.
/// </summary>
public static class VersionHeader
{
    /// <summary>
    /// The letter in front of the Wintty half. Rendered here, enforced by
    /// the release tag trigger: Wintty tags are <c>w*</c> because the fork
    /// inherits ghostty's <c>v*</c> tags and cannot share that namespace.
    /// </summary>
    public const string TagPrefix = "w";

    /// <summary>
    /// The cadence word the build was stamped with (<c>stable</c> or
    /// <c>tip</c>), recovered from <paramref name="winttyVersionString"/>,
    /// which is <c>&lt;winttyVersion&gt;-&lt;cadence&gt;+&lt;commit&gt;</c>.
    /// Empty when the string does not have that shape, so a caller renders
    /// nothing rather than a guess. The version itself may carry a
    /// prerelease (<c>1.0.0-rc.1</c>), which is why this anchors on the
    /// whole version rather than the last dash.
    /// </summary>
    public static string Cadence(string winttyVersion, string winttyVersionString)
    {
        if (string.IsNullOrEmpty(winttyVersion) || string.IsNullOrEmpty(winttyVersionString))
            return string.Empty;

        var plus = winttyVersionString.IndexOf('+');
        var prerelease = plus >= 0 ? winttyVersionString[..plus] : winttyVersionString;

        var prefix = winttyVersion + "-";
        if (!prerelease.StartsWith(prefix, StringComparison.Ordinal))
            return string.Empty;

        var cadence = prerelease[prefix.Length..];
        // A cadence is a bare word (stable, tip). Anything with digits or
        // punctuation is a prerelease identifier that leaked past the
        // version, and rendering it as "(rc.1)" would be a lie.
        if (cadence.Length == 0) return string.Empty;
        foreach (var c in cadence)
        {
            if (!char.IsAsciiLetterLower(c)) return string.Empty;
        }
        return cadence;
    }

    /// <summary>
    /// <c>w1.0.0-rc.1 (tip) on libghostty v1.3.2-dev</c>. The cadence is
    /// omitted when it cannot be recovered, the libghostty half when the
    /// library reports no version.
    /// </summary>
    public static string ComposeVersion(VersionInfo info)
    {
        var sb = new StringBuilder();
        sb.Append(TagPrefix).Append(info.WinttyVersion);

        var cadence = Cadence(info.WinttyVersion, info.WinttyVersionString);
        if (cadence.Length > 0)
            sb.Append(" (").Append(cadence).Append(')');

        if (!string.IsNullOrEmpty(info.LibGhostty.Version))
            sb.Append(" on libghostty v").Append(info.LibGhostty.Version);

        return sb.ToString();
    }

    /// <summary>
    /// <see cref="ComposeVersion"/> with the product name in front, for the
    /// places that stand alone: the <c>+version</c> header, log headers, the
    /// Version dialog title. The About window uses the bare form because
    /// its heading already carries the name.
    /// </summary>
    public static string Compose(VersionInfo info)
        => AppIdentity.ProductName + " " + ComposeVersion(info);
}
