namespace Ghostty.Core.Gaps;

/// <summary>
/// A gap issue body as GitHub renders a submitted form: <c>### &lt;label&gt;</c>
/// headings, each followed by the field's value.
/// </summary>
/// <remarks>
/// Every search hit is untrusted text anyone could have written. A hit counts
/// for a key only if <see cref="MatchesKey"/> holds: the token field equals the
/// key's token, the key field equals the readable key and hashes to that token,
/// and no other token appears anywhere in the body.
/// </remarks>
public sealed class RenderedIssue
{
    private readonly Dictionary<string, string> _fields;
    private readonly IReadOnlySet<string> _tokens;

    private RenderedIssue(Dictionary<string, string> fields, IReadOnlySet<string> tokens)
    {
        _fields = fields;
        _tokens = tokens;
    }

    /// <summary>Bodies over this size are skipped by the lookup, never parsed.</summary>
    public const int MaxBodyChars = 64 * 1024;

    public static RenderedIssue? Parse(string? body)
    {
        if (body is null || body.Length > MaxBodyChars) return null;

        var labelToId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, labels) in GapForm.AcceptedLabels)
            foreach (var label in labels)
                labelToId[label] = id;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentId = null;
        var value = new List<string>();

        void Flush()
        {
            if (currentId is null) return;
            var text = string.Join('\n', value).Trim();
            // First occurrence wins: a duplicated heading cannot override a field.
            fields.TryAdd(currentId, text == GapForm.NoResponse ? string.Empty : text);
        }

        foreach (var rawLine in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                var label = rawLine[4..].Trim();
                currentId = labelToId.TryGetValue(label, out var id) ? id : null;
                value.Clear();
                continue;
            }
            if (currentId is not null) value.Add(rawLine);
        }
        Flush();

        return new RenderedIssue(fields, GapToken.FindAll(body));
    }

    /// <summary>The field value with code-span backticks removed, or empty.</summary>
    public string Field(string id) =>
        _fields.TryGetValue(id, out var v) ? v.Trim('`').Trim() : string.Empty;

    public bool MatchesKey(GapKey key) =>
        Field(GapForm.TokenId) == key.Token
        && Field(GapForm.KeyId) == key.Qualified
        && _tokens.Count == 1
        && _tokens.Contains(key.Token);
}
