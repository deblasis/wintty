using Ghostty.Core.Version;
using Xunit;

namespace Ghostty.Tests.Version;

public sealed class EditionLabelTests
{
    [Fact]
    public void Format_Oss_ReturnsLowercaseOss()
    {
        Assert.Equal("oss", EditionLabel.Format(Edition.Oss));
    }
}
