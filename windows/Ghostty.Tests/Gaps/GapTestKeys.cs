using Ghostty.Core.Gaps;

namespace Ghostty.Tests.Gaps;

/// <summary>Keys for tests only; production keys live in <see cref="GapCatalogue"/>.</summary>
internal static class GapTestKeys
{
    public static readonly GapKey Tracked = new("herdr", "pane.placement.zoomed", GapDisposition.Tracked);
    public static readonly GapKey Prefix = new("herdr", "cli.pane", GapDisposition.Tracked);
    public static readonly GapKey PrefixLonger = new("herdr", "cli.pane.move", GapDisposition.Tracked);
    public static readonly GapKey ByDesign = new("herdr", "host.takeover", GapDisposition.ByDesign, "Plugins restart when the window hosting them closes.");
    public static readonly GapKey NotApplicable = new("ghostty.config", "macos-titlebar-style", GapDisposition.NotApplicable, "Not used on Windows.");
    public static readonly GapKey UnknownVerb = new("herdr", "unknown.verb", GapDisposition.Tracked);

    private static readonly GapKey[] s_all = [Tracked, Prefix, PrefixLonger, ByDesign, NotApplicable, UnknownVerb];

    public static GapKey? Lookup(string qualified) =>
        s_all.FirstOrDefault(k => k.Qualified == qualified);

    /// <summary>A body shaped like GitHub's rendering of a submitted gap form.</summary>
    public static string FormBody(GapKey key, string? rawName = null, string? extra = null) =>
        $"""
        ### Gap token

        {key.Token}

        ### Gap key

        {key.Qualified}

        ### Unsupported name

        {(rawName is null ? "_No response_" : "`" + rawName + "`")}

        ### Wintty version

        `1.0.0` (pro)

        ### Details

        {extra ?? "_No response_"}
        """;
}
