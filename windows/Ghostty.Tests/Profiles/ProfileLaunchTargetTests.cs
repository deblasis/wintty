using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public class ProfileLaunchTargetTests
{
    [Fact]
    public void Values_AreInDocumentedOrder()
    {
        Assert.Equal(0, (int)ProfileLaunchTarget.NewTab);
        Assert.Equal(1, (int)ProfileLaunchTarget.NewPane);
        Assert.Equal(2, (int)ProfileLaunchTarget.NewWindow);
    }
}
