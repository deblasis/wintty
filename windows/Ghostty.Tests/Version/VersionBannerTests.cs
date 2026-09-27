using System;
using Ghostty.Core;
using Ghostty.Core.Version;
using Xunit;

namespace Ghostty.Tests.Version;

public class VersionBannerTests
{
    [Fact]
    public void Header_is_one_line_starting_with_the_product_name()
    {
        var header = VersionBanner.Header();

        // One line is load-bearing: this string is the Message of one
        // rolling-log record and one line of gpu.log and the crash log,
        // and every reader of those files anchors per line.
        Assert.DoesNotContain('\n', header);
        Assert.DoesNotContain('\r', header);

        // Product name, then the w-prefixed version the release tags use.
        Assert.StartsWith(AppIdentity.ProductName + " w", header);
        Assert.Contains(BuildInfo.WinttyVersion, header);
    }

    [Fact]
    public void Header_is_stable_across_calls()
    {
        // Cached once: the crash path must not re-enter libghostty, and
        // every artifact in one launch must carry the identical line.
        Assert.Same(VersionBanner.Header(), VersionBanner.Header());
    }
}
