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

        // The edition rides on the banner line (and only there - the
        // shared VersionHeader.Compose is left to the +version header and
        // the dialog title). The overlay's channel-aware labels land the
        // same way, so the assert pins the formatter's own output rather
        // than a spelled-out tier name.
        Assert.EndsWith("(" + EditionLabel.Format(BuildInfo.Edition) + ")", header);
    }

    [Fact]
    public void Header_is_stable_across_calls()
    {
        // Cached once: the crash path must not re-enter libghostty, and
        // every artifact in one launch must carry the identical line.
        Assert.Same(VersionBanner.Header(), VersionBanner.Header());
    }
}
