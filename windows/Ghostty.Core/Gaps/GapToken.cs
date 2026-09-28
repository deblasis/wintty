using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ghostty.Core.Gaps;

/// <summary>
/// The de-duplication token carried in every gap issue.
/// </summary>
/// <remarks>
/// <c>wgap-</c> plus the first 12 hex digits of SHA-256 over
/// <c>v1|&lt;domain&gt;|&lt;name&gt;</c>. It depends on the key alone, never on the
/// source that raised the gap, so every report of the same missing feature
/// carries the same token. GitHub search splits on the hyphen, so the 12-hex
/// part gives recall and exactness comes from comparing the parsed form field
/// on the client (<see cref="RenderedIssue"/>).
/// </remarks>
public static partial class GapToken
{
    public const string Prefix = "wgap-";

    public static string Compute(string domain, string name)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("v1|" + domain + "|" + name), hash);
        return Prefix + Convert.ToHexStringLower(hash[..6]);
    }

    /// <summary>Every distinct token-shaped string in <paramref name="text"/>.</summary>
    public static IReadOnlySet<string> FindAll(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in TokenPattern().Matches(text))
            found.Add(m.Value);
        return found;
    }

    [GeneratedRegex(@"wgap-[0-9a-f]{12}(?![0-9a-f])", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
