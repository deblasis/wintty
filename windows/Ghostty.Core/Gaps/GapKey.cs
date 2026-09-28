namespace Ghostty.Core.Gaps;

/// <summary>
/// One thing Wintty does not support, identified by a domain and a name,
/// for example domain <c>herdr</c> and name <c>pane.placement.zoomed</c>.
/// </summary>
/// <remarks>
/// The catalogue is closed: keys are created only in <see cref="GapCatalogue"/>,
/// never from runtime text. That is what lets fifty plugins needing the same
/// missing feature produce one token and one issue, and what keeps a plugin
/// from minting keys (or report titles) of its own. A test fails if any other
/// file in Ghostty.Core constructs a key.
/// </remarks>
public sealed class GapKey
{
    internal GapKey(string domain, string name, GapDisposition disposition, string? explanation = null)
    {
        Domain = domain;
        Name = name;
        Disposition = disposition;
        Explanation = explanation;
        Token = GapToken.Compute(domain, name);
    }

    /// <summary>The area the gap belongs to, e.g. <c>herdr</c> or <c>ghostty.config</c>.</summary>
    public string Domain { get; }

    /// <summary>The name within the domain, e.g. <c>pane.placement.zoomed</c>.</summary>
    public string Name { get; }

    /// <summary><c>&lt;domain&gt;.&lt;name&gt;</c>, the readable form shown to users and in issues.</summary>
    public string Qualified => Domain + "." + Name;

    /// <summary>The compiled-in disposition. <c>gaps.json</c> can override it only for
    /// <see cref="GapDisposition.Tracked"/>, <see cref="GapDisposition.Planned"/> and
    /// <see cref="GapDisposition.Fixed"/>.</summary>
    public GapDisposition Disposition { get; }

    /// <summary>Fixed copy shown for <see cref="GapDisposition.ByDesign"/> and
    /// <see cref="GapDisposition.NotApplicable"/> keys; null otherwise.</summary>
    public string? Explanation { get; }

    /// <summary>The de-duplication token, <c>wgap-</c> plus 12 hex digits.</summary>
    public string Token { get; }

    /// <summary>
    /// True for the catch-all keys that stand for names a newer herdr has and
    /// the catalogue does not (<c>herdr.unknown.&lt;kind&gt;</c>). These are never
    /// bound to an issue, so their Report action always stays available, and
    /// search hits for them match only on the same raw name.
    /// </summary>
    public bool IsUnknownBucket => Name.StartsWith("unknown.", System.StringComparison.Ordinal);

    public override string ToString() => Qualified;
}
