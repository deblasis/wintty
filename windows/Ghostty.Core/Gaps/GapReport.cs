using System.Text;
using System.Text.RegularExpressions;

namespace Ghostty.Core.Gaps;

/// <summary>What the user chose to include when reporting a gap.</summary>
public sealed record GapReportRequest(
    GapKey Key,
    string WinttyVersion,
    string Tier,
    string? EmulatedHerdrVersion = null,
    string? RequestedMinHerdrVersion = null,
    string? RawName = null,
    bool IncludeRawName = false,
    string? PluginName = null,
    bool IncludePluginName = false,
    int? RegressionOf = null,
    string? Details = null);

/// <summary>The prefilled issue-form URL and the same content as pasteable text.</summary>
/// <param name="Url">The <c>issues/new</c> URL, or null for domains that have no form.</param>
/// <param name="CopyText">Title and body in the rendered-form shape, for Copy report.</param>
/// <param name="Overflow">Text dropped from the URL to keep it under the cap, for the clipboard; empty when nothing was dropped.</param>
public sealed record GapReport(string? Url, string CopyText, string Overflow);

/// <summary>
/// Builds the prefilled issue for a gap.
/// </summary>
/// <remarks>
/// <para>
/// Everything that reaches the URL or body is either compiled in (key, token,
/// form, labels), a Wintty version string, or user-opted text that passes a
/// strict pattern and renders inside a code span so GitHub never links it.
/// Never paths, usernames, machine names, config values or tokens.
/// </para>
/// <para>
/// The URL passes only <c>template</c>, <c>title</c> and form field ids; never
/// <c>labels</c>, <c>assignees</c> or <c>milestone</c>, which reporters without
/// permission cannot set (GitHub answers such URLs with a 404). The form's own
/// labels apply <c>gap</c> and the domain label.
/// </para>
/// <para>
/// The URL stays under <see cref="MaxUrlLength"/>: <c>details</c> is dropped
/// first, then <c>herdr_version</c>; the token, key and <c>regression_of</c>
/// are never dropped. Dropped text is returned for the clipboard.
/// </para>
/// </remarks>
public static partial class GapReportBuilder
{
    /// <summary>Below <c>INTERNET_MAX_URL_LENGTH</c> (2083), which some ShellExecute paths enforce.</summary>
    public const int MaxUrlLength = 2000;

    public static GapReport Build(GapReportRequest r)
    {
        var fields = new List<(string Id, string Label, string Value)>
        {
            (GapForm.TokenId, GapForm.TokenLabel, r.Key.Token),
            (GapForm.KeyId, GapForm.KeyLabel, r.Key.Qualified),
        };

        if (r.IncludeRawName && IsValidRawName(r.RawName))
            fields.Add((GapForm.RawNameId, GapForm.RawNameLabel, Code(r.RawName!)));
        if (r.IncludePluginName && IsValidPluginName(r.PluginName))
            fields.Add((GapForm.PluginNameId, GapForm.PluginNameLabel, Code(r.PluginName!)));

        fields.Add((GapForm.WinttyVersionId, GapForm.WinttyVersionLabel,
            $"{Code(SafeVersion(r.WinttyVersion))} ({SafeTier(r.Tier)})"));

        string? herdr = null;
        if (r.EmulatedHerdrVersion is not null)
        {
            herdr = "emulated " + Code(SafeVersion(r.EmulatedHerdrVersion));
            if (r.RequestedMinHerdrVersion is not null)
                herdr += "; plugin needs " + (ReleaseVersion.TryParse(r.RequestedMinHerdrVersion, out var v)
                    ? Code(v.Core)
                    : "`<invalid>`");
            fields.Add((GapForm.HerdrVersionId, GapForm.HerdrVersionLabel, herdr));
        }

        if (r.RegressionOf is int reg and > 0)
            fields.Add((GapForm.RegressionOfId, GapForm.RegressionOfLabel, "#" + reg));

        var details = r.Details is null ? null : Sanitize(r.Details);
        if (!string.IsNullOrEmpty(details))
            fields.Add((GapForm.DetailsId, GapForm.DetailsLabel, details));

        var title = "[gap] " + r.Key.Qualified;
        var copy = new StringBuilder();
        copy.Append(title).Append("\n\n");
        foreach (var (_, label, value) in fields)
            copy.Append("### ").Append(label).Append("\n\n").Append(value).Append("\n\n");

        var form = GapCatalogue.FormFor(r.Key.Domain);
        if (form is null)
            return new GapReport(null, copy.ToString().TrimEnd() + "\n", string.Empty);

        var overflow = new StringBuilder();
        var url = UrlFor(form, title, fields);
        foreach (var droppable in new[] { GapForm.DetailsId, GapForm.HerdrVersionId })
        {
            if (url.Length <= MaxUrlLength) break;
            var idx = fields.FindIndex(f => f.Id == droppable);
            if (idx < 0) continue;
            overflow.Append("### ").Append(fields[idx].Label).Append("\n\n").Append(fields[idx].Value).Append("\n\n");
            fields.RemoveAt(idx);
            url = UrlFor(form, title, fields);
        }

        return new GapReport(url.Length <= MaxUrlLength ? url : null,
            copy.ToString().TrimEnd() + "\n", overflow.ToString().TrimEnd());
    }

    public static bool IsValidRawName(string? name) =>
        name is not null && RawNamePattern().IsMatch(name)
        && !name.StartsWith("wgap", StringComparison.Ordinal);

    public static bool IsValidPluginName(string? name) =>
        name is not null && PluginNamePattern().IsMatch(name)
        && !name.Contains("wgap", StringComparison.Ordinal);

    private static string UrlFor(string form, string title, List<(string Id, string Label, string Value)> fields)
    {
        var sb = new StringBuilder("https://github.com/")
            .Append(GapForm.Repository)
            .Append("/issues/new?template=").Append(Uri.EscapeDataString(form))
            .Append("&title=").Append(Uri.EscapeDataString(title));
        foreach (var (id, _, value) in fields)
            sb.Append('&').Append(id).Append('=').Append(Uri.EscapeDataString(value));
        return sb.ToString();
    }

    private static string Code(string s) => "`" + s + "`";

    private static string SafeVersion(string v) =>
        ReleaseVersion.TryParse(v, out var parsed) ? parsed.ToString() : "<invalid>";

    private static string SafeTier(string tier) =>
        TierPattern().IsMatch(tier) ? tier : "unknown";

    // User-typed details: plain text only, bidi and control characters
    // removed, capped. The user wrote it and sees it before submitting.
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 1000));
        foreach (var ch in s)
        {
            if (sb.Length >= 1000) break;
            if (ch == '\n') { sb.Append('\n'); continue; }
            if (char.IsControl(ch) || IsBidiControl(ch)) continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static bool IsBidiControl(char ch) =>
        ch is '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u061C'
            or (>= '\u200B' and <= '\u200D') or '\uFEFF';

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex RawNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginNamePattern();

    [GeneratedRegex(@"^[a-z][a-z-]{0,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex TierPattern();
}
