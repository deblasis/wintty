using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Profiles;
using Xunit;

namespace Ghostty.Tests.Input;

public class ProfileSlotResolverTests
{
    [Theory]
    [InlineData(1, "p1")]
    [InlineData(2, "p2")]
    [InlineData(5, "p5")]
    [InlineData(9, "p9")]
    public void Resolve_InRange_ReturnsProfileIdAtIndexNMinus1(int slot, string expectedId)
    {
        var profiles = FakeProfileRegistry.BuildN(9);
        Assert.Equal(expectedId, ProfileSlotResolver.Resolve(profiles, slot));
    }

    [Fact]
    public void Resolve_SlotPastEnd_ReturnsNull()
    {
        var profiles = FakeProfileRegistry.BuildN(3);
        Assert.Null(ProfileSlotResolver.Resolve(profiles, 5));
    }

    [Fact]
    public void Resolve_EmptyList_ReturnsNull()
    {
        Assert.Null(ProfileSlotResolver.Resolve([], 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Resolve_NonPositiveSlot_ReturnsNull(int slot)
    {
        var profiles = FakeProfileRegistry.BuildN(3);
        Assert.Null(ProfileSlotResolver.Resolve(profiles, slot));
    }
}
