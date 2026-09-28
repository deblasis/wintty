namespace Ghostty.Core.Gaps;

/// <summary>What Wintty does about a gap, which decides what its row offers.</summary>
public enum GapDisposition
{
    /// <summary>A real missing feature; the row offers Report.</summary>
    Tracked,

    /// <summary>Irrelevant on Windows (e.g. <c>macos-*</c> config keys); no Report, no lookup.</summary>
    NotApplicable,

    /// <summary>Wintty's intended behaviour, not a missing feature; no Report, no lookup.</summary>
    ByDesign,

    /// <summary>Accepted and tracked on a bound issue; the row links it instead of offering Report.</summary>
    Planned,

    /// <summary>Fixed in a release named by <c>fixed-in</c>.</summary>
    Fixed,
}
