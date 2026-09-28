using System.Security.Cryptography;
using System.Text;
using Ghostty.Core.Gaps;
using Xunit;

namespace Ghostty.Tests.Gaps;

public class GapTokenTests
{
    [Fact]
    public void Token_is_prefix_plus_first_twelve_hex_of_sha256_over_versioned_key()
    {
        var expected = "wgap-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("v1|herdr|pane.placement.zoomed")))[..12];

        Assert.Equal(expected, GapTestKeys.Tracked.Token);
    }

    [Fact]
    public void Token_depends_on_the_key_alone()
    {
        // Two different sources raising the same gap must land on one issue.
        Assert.Equal(GapToken.Compute("herdr", "pane.placement.zoomed"), GapTestKeys.Tracked.Token);
    }

    [Fact]
    public void Prefix_keys_get_different_tokens()
    {
        Assert.NotEqual(GapTestKeys.Prefix.Token, GapTestKeys.PrefixLonger.Token);
    }

    [Fact]
    public void FindAll_returns_distinct_tokens_and_ignores_longer_hex_runs()
    {
        var a = GapTestKeys.Tracked.Token;
        var text = $"{a} and {a} again, plus {GapTestKeys.Prefix.Token}, not wgap-0123456789abc";

        var found = GapToken.FindAll(text);

        Assert.Equal(2, found.Count);
        Assert.Contains(a, found);
        Assert.DoesNotContain("wgap-0123456789ab", found);
    }
}
