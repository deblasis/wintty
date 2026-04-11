namespace Ghostty.Core.Config;

/// <summary>
/// Parsed representation of a single line from a ghostty config file.
/// The format is: key = value (one per line, # for comments).
/// </summary>
public readonly record struct ConfigLine(
    string? Key,
    string? Value,
    bool IsComment,
    bool IsEmpty)
{
    public static ConfigLine Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ConfigLine(null, null, false, true);

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('#'))
            return new ConfigLine(null, null, true, false);

        var eqIndex = trimmed.IndexOf('=');
        if (eqIndex < 0)
            return new ConfigLine(null, null, false, false);

        var key = trimmed[..eqIndex].TrimEnd();
        var value = trimmed[(eqIndex + 1)..].TrimStart();
        return new ConfigLine(key, value, false, false);
    }
}
