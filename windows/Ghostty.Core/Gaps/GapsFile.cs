using System.Text.Json;

namespace Ghostty.Core.Gaps;

/// <summary>One maintainer decision from <c>gaps.json</c>.</summary>
public sealed record GapsFileEntry(GapDisposition Disposition, int? Issue, ReleaseVersion? FixedIn);

/// <summary>
/// The maintainer-written <c>gaps.json</c> on the public repo's default branch:
/// dispositions and canonical-issue bindings that override the compiled
/// catalogue.
/// </summary>
/// <remarks>
/// <para>
/// Parsed strictly and defensively, because it is fetched over the network
/// and read by every installed build:
/// </para>
/// <list type="bullet">
/// <item>keys not in the compiled catalogue are ignored;</item>
/// <item>an unknown disposition or field is read as <see cref="GapDisposition.Tracked"/>,
/// so an older app never hides Report on something it does not understand;</item>
/// <item><see cref="GapDisposition.ByDesign"/> and <see cref="GapDisposition.NotApplicable"/>
/// are compiled-in only; the file can neither set nor clear them;</item>
/// <item><c>herdr.unknown.*</c> keys are never bound, so any entry for one is ignored;</item>
/// <item><c>fixed</c> needs a valid <c>fixed-in</c>; an issue must be a positive integer.</item>
/// </list>
/// <para>
/// No text or URL from the file is ever rendered. Issue numbers are shown only
/// as <c>#N</c> linking to the compiled repository.
/// </para>
/// </remarks>
public sealed class GapsFile
{
    public const int SupportedVersion = 1;
    public const int MaxBytes = 256 * 1024;

    private readonly Dictionary<string, GapsFileEntry> _entries;

    private GapsFile(Dictionary<string, GapsFileEntry> entries) => _entries = entries;

    public static GapsFile Empty { get; } = new(new Dictionary<string, GapsFileEntry>(StringComparer.Ordinal));

    public bool TryGet(GapKey key, out GapsFileEntry entry) =>
        _entries.TryGetValue(key.Qualified, out entry!);

    /// <summary>Parses the file; malformed input yields <see cref="Empty"/>, never an exception.</summary>
    public static GapsFile Parse(ReadOnlySpan<byte> utf8, Func<string, GapKey?> lookup)
    {
        if (utf8.Length == 0 || utf8.Length > MaxBytes) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;
            if (!root.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number
                || !ver.TryGetInt32(out var v) || v < 1)
                return Empty;
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                return Empty;

            var result = new Dictionary<string, GapsFileEntry>(StringComparer.Ordinal);
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) continue;
                var key = lookup(keyEl.GetString()!);
                if (key is null || key.IsUnknownBucket) continue;
                if (key.Disposition is GapDisposition.ByDesign or GapDisposition.NotApplicable) continue;

                var disposition = ReadDisposition(item);
                if (disposition is GapDisposition.ByDesign or GapDisposition.NotApplicable) continue;

                int? issue = null;
                if (item.TryGetProperty("issue", out var issueEl))
                {
                    if (issueEl.ValueKind != JsonValueKind.Number || !issueEl.TryGetInt32(out var n) || n <= 0)
                        continue;
                    issue = n;
                }

                ReleaseVersion? fixedIn = null;
                if (item.TryGetProperty("fixed_in", out var fixedEl))
                {
                    if (fixedEl.ValueKind != JsonValueKind.String
                        || !ReleaseVersion.TryParse(fixedEl.GetString(), out var fv))
                        continue;
                    fixedIn = fv;
                }
                if (disposition == GapDisposition.Fixed && fixedIn is null) continue;
                if (disposition == GapDisposition.Planned && issue is null) continue;

                result.TryAdd(key.Qualified, new GapsFileEntry(disposition, issue, fixedIn));
            }
            return new GapsFile(result);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static GapDisposition ReadDisposition(JsonElement item)
    {
        if (!item.TryGetProperty("disposition", out var d) || d.ValueKind != JsonValueKind.String)
            return GapDisposition.Tracked;
        return d.GetString() switch
        {
            "planned" => GapDisposition.Planned,
            "fixed" => GapDisposition.Fixed,
            "by-design" => GapDisposition.ByDesign,
            "not-applicable" => GapDisposition.NotApplicable,
            _ => GapDisposition.Tracked,
        };
    }
}
