namespace Ghostty.Core.Gaps;

/// <summary>
/// The closed, compiled-in list of gaps, and the one place <see cref="GapKey"/>s
/// are created.
/// </summary>
/// <remarks>
/// <para>
/// Domains beyond <c>wintty</c> are added by their consumers: generated
/// <c>ghostty.config.*</c> keys from the pinned config schema, and the
/// <c>herdr.*</c> domain, which compiles only into the pro family.
/// </para>
/// <para>
/// Keys in the <c>wintty</c> domain are Wintty-internal and have no issue
/// form. <see cref="CanarySearch"/> is reserved for the monthly search canary
/// and is never raised by any code path.
/// </para>
/// </remarks>
public static class GapCatalogue
{
    public const string WinttyDomain = "wintty";

    /// <summary>Reserved token for the permanent search canary issue. Never raised.</summary>
    public static readonly GapKey CanarySearch =
        new(WinttyDomain, "canary.search", GapDisposition.Planned);

    /// <summary>A plugin asked for a newer Wintty than this one. The fix is updating Wintty.</summary>
    public static readonly GapKey WinttyVersionMinNewer =
        new(WinttyDomain, "version.min_newer", GapDisposition.ByDesign,
            "This plugin was written for a newer Wintty. Update Wintty to get the features it expects.");

    private static readonly GapKey[] s_all = [CanarySearch, WinttyVersionMinNewer];

    private static readonly Dictionary<string, GapKey> s_byQualified =
        s_all.ToDictionary(k => k.Qualified, StringComparer.Ordinal);

    public static IReadOnlyList<GapKey> All => s_all;

    public static bool TryGet(string qualified, out GapKey key) =>
        s_byQualified.TryGetValue(qualified, out key!);

    /// <summary>
    /// The issue-form template for a domain, or null for domains that are
    /// never reported (<c>wintty</c>). One form per domain, so the form's own
    /// labels apply the domain label for reporters who cannot set labels.
    /// </summary>
    public static string? FormFor(string domain) => domain switch
    {
        WinttyDomain => null,
        _ => "gap-" + domain.Replace('.', '-') + ".yml",
    };
}
