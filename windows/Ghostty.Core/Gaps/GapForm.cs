namespace Ghostty.Core.Gaps;

/// <summary>
/// Field ids and labels of the gap issue forms on the public repo.
/// </summary>
/// <remarks>
/// Frozen once shipped: GitHub renders each form field as
/// <c>### &lt;label&gt;</c> followed by its value, and the lookup parses bodies
/// filed by every earlier form revision. A rename adds the new label to
/// <see cref="AcceptedLabels"/> and never removes the old one; a tooling test
/// compares these with the committed form YAML.
/// </remarks>
public static class GapForm
{
    public const string Repository = "deblasis/wintty";

    public const string TokenId = "gap_token";
    public const string KeyId = "gap_key";
    public const string RawNameId = "raw_name";
    public const string PluginNameId = "plugin_name";
    public const string WinttyVersionId = "wintty_version";
    public const string HerdrVersionId = "herdr_version";
    public const string RegressionOfId = "regression_of";
    public const string DetailsId = "details";

    public const string TokenLabel = "Gap token";
    public const string KeyLabel = "Gap key";
    public const string RawNameLabel = "Unsupported name";
    public const string PluginNameLabel = "Plugin name";
    public const string WinttyVersionLabel = "Wintty version";
    public const string HerdrVersionLabel = "herdr version";
    public const string RegressionOfLabel = "Regression of";
    public const string DetailsLabel = "Details";

    /// <summary>What GitHub renders for an optional field left empty.</summary>
    public const string NoResponse = "_No response_";

    /// <summary>Every label ever shipped, per field id.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AcceptedLabels =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [TokenId] = [TokenLabel],
            [KeyId] = [KeyLabel],
            [RawNameId] = [RawNameLabel],
            [PluginNameId] = [PluginNameLabel],
            [WinttyVersionId] = [WinttyVersionLabel],
            [HerdrVersionId] = [HerdrVersionLabel],
            [RegressionOfId] = [RegressionOfLabel],
            [DetailsId] = [DetailsLabel],
        };

    /// <summary>Label the issue form applies; only maintainers apply <see cref="TriagedLabel"/>.</summary>
    public const string GapLabel = "gap";
    public const string TriagedLabel = "gap:triaged";
}
