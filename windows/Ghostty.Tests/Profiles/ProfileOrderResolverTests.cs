using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class ProfileOrderResolverTests
{
    private static ProfileDef User(string id, bool hidden = false)
        => new(Id: id, Name: id, Command: $"{id}.exe", Hidden: hidden);

    private static DiscoveredProfile Disc(string id, string probe = "test")
        => new(Id: id, Name: id, Command: $"{id}.exe", ProbeId: probe);

    [Fact]
    public void Resolve_NoProfileOrder_UserFirstThenDiscoveredAlpha()
    {
        var users = new[] { User("alpha"), User("zulu") };
        var discovered = new[] { Disc("delta"), Disc("bravo") };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: discovered,
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.Equal(new[] { "alpha", "zulu", "bravo", "delta" }, resolved.Select(r => r.Id));
    }

    [Fact]
    public void Resolve_UserAndDiscoveredSameId_UserWins()
    {
        var users = new[] { User("pwsh") with { Name = "MyPwsh" } };
        var discovered = new[] { Disc("pwsh") };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: discovered,
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.Single(resolved);
        Assert.Equal("MyPwsh", resolved[0].Name);
        Assert.Null(resolved[0].ProbeId);
    }

    [Fact]
    public void Resolve_HiddenProfile_OmittedFromResult()
    {
        var users = new[] { User("a"), User("b", hidden: true), User("c") };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.Equal(new[] { "a", "c" }, resolved.Select(r => r.Id));
    }

    [Fact]
    public void Resolve_HiddenSet_OverridesNonHiddenDef()
    {
        var users = new[] { User("a"), User("b") };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string> { "a" }).Visible.ToList();

        Assert.Equal(new[] { "b" }, resolved.Select(r => r.Id));
    }

    [Fact]
    public void Resolve_ProfileOrder_ListedFirstThenUnlistedByDefault()
    {
        var users = new[] { User("alpha"), User("zulu") };
        var discovered = new[] { Disc("bravo"), Disc("delta") };
        var order = new[] { "delta", "alpha" };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: discovered,
            profileOrder: order,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.Equal(new[] { "delta", "alpha", "zulu", "bravo" }, resolved.Select(r => r.Id));
    }

    [Fact]
    public void Resolve_ProfileOrderHasUnknownIds_SilentlyIgnoresThem()
    {
        var users = new[] { User("a") };
        var order = new[] { "ghost", "a", "phantom" };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: order,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.Equal(new[] { "a" }, resolved.Select(r => r.Id));
    }

    [Fact]
    public void Resolve_DefaultProfileId_MarkedIsDefault()
    {
        var users = new[] { User("alpha"), User("zulu") };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: null,
            defaultProfileId: "zulu",
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.False(resolved.Single(r => r.Id == "alpha").IsDefault);
        Assert.True(resolved.Single(r => r.Id == "zulu").IsDefault);
    }

    [Fact]
    public void Resolve_DefaultProfileIdMissing_FallsBackToFirst()
    {
        var users = new[] { User("alpha"), User("zulu") };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: null,
            defaultProfileId: "ghost",
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.True(resolved[0].IsDefault);
        Assert.Equal("alpha", resolved[0].Id);
    }

    [Fact]
    public void Resolve_DefaultProfileIdNull_FirstIsDefault()
    {
        var users = new[] { User("alpha") };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.True(resolved[0].IsDefault);
    }

    [Fact]
    public void Resolve_OrderIndex_ContiguousFromZero()
    {
        var users = new[] { User("a"), User("b"), User("c") };

        var resolved = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.Equal(0, resolved[0].OrderIndex);
        Assert.Equal(1, resolved[1].OrderIndex);
        Assert.Equal(2, resolved[2].OrderIndex);
    }

    [Fact]
    public void Resolve_DiscoveredProfile_HasProbeIdSet()
    {
        var resolved = ProfileOrderResolver.Resolve(
            user: [],
            discovered: new[] { Disc("wsl-ubuntu", probe: "wsl") },
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>()).Visible.Single();

        Assert.Equal("wsl", resolved.ProbeId);
    }

    [Fact]
    public void Resolve_EmptyInputs_ReturnsEmpty()
    {
        var resolved = ProfileOrderResolver.Resolve(
            user: [],
            discovered: [],
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>());

        Assert.Empty(resolved.Visible);
    }

    [Fact]
    public void Resolve_DiscoveredAddedLater_DoesNotShiftUserSlots()
    {
        var users = new[] { User("pwsh-7"), User("cmd"), User("git-bash") };

        var before = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: null,
            defaultProfileId: "pwsh-7",
            hiddenIds: new HashSet<string>()).Visible.ToList();

        var after = ProfileOrderResolver.Resolve(
            user: users,
            discovered: new[] { Disc("wsl-ubuntu"), Disc("wsl-debian") },
            profileOrder: null,
            defaultProfileId: "pwsh-7",
            hiddenIds: new HashSet<string>()).Visible.ToList();

        Assert.Equal("pwsh-7", before[0].Id);
        Assert.Equal("pwsh-7", after[0].Id);
        Assert.Equal("cmd", before[1].Id);
        Assert.Equal("cmd", after[1].Id);
        Assert.Equal("git-bash", before[2].Id);
        Assert.Equal("git-bash", after[2].Id);

        Assert.Equal(new[] { "wsl-debian", "wsl-ubuntu" },
                     after.Skip(3).Select(r => r.Id));
    }

    [Fact]
    public void Resolve_HidingDiscovered_FreesSlotForLowerEntries()
    {
        var discovered = new[] { Disc("wsl-debian"), Disc("wsl-kali"), Disc("wsl-ubuntu") };

        var noHide = ProfileOrderResolver.Resolve(
            user: [],
            discovered: discovered,
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>()).Visible.ToList();

        var hideKali = ProfileOrderResolver.Resolve(
            user: [],
            discovered: discovered,
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string> { "wsl-kali" }).Visible.ToList();

        Assert.Equal(new[] { "wsl-debian", "wsl-kali", "wsl-ubuntu" },
                     noHide.Select(r => r.Id));
        Assert.Equal(new[] { "wsl-debian", "wsl-ubuntu" },
                     hideKali.Select(r => r.Id));

        Assert.Equal("wsl-debian", hideKali[0].Id);
        Assert.Equal("wsl-ubuntu", hideKali[1].Id);
    }

    [Fact]
    public void Resolve_ExplicitProfileOrderPinsSlotIndependentOfHide()
    {
        var users = new[] { User("a"), User("b"), User("c"), User("d") };

        var pinned = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: new[] { "c", "a" },
            defaultProfileId: null,
            hiddenIds: new HashSet<string> { "b" }).Visible.ToList();

        Assert.Equal(new[] { "c", "a", "d" }, pinned.Select(r => r.Id));
    }

    [Fact]
    public void Resolve_HiddenSet_RetainedInHiddenListInOriginalOrder()
    {
        var users = new[] { User("alpha"), User("zulu", hidden: true) };
        var discovered = new[] { Disc("bravo") };

        var set = ProfileOrderResolver.Resolve(
            user: users,
            discovered: discovered,
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string> { "bravo" });

        Assert.Equal(new[] { "alpha" }, set.Visible.Select(r => r.Id));
        Assert.Equal(new[] { "zulu", "bravo" }, set.Hidden.Select(r => r.Id));
    }

    [Fact]
    public void Resolve_HiddenEntries_OrderIndexRestartsAtZero()
    {
        var users = new[] { User("a"), User("b", hidden: true), User("c", hidden: true) };

        var set = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>());

        Assert.Equal(new[] { 0 }, set.Visible.Select(r => r.OrderIndex));
        Assert.Equal(new[] { 0, 1 }, set.Hidden.Select(r => r.OrderIndex));
    }

    [Fact]
    public void Resolve_HiddenEntry_NeverMarkedDefault()
    {
        var users = new[] { User("a", hidden: true), User("b") };

        var set = ProfileOrderResolver.Resolve(
            user: users,
            discovered: [],
            profileOrder: null,
            defaultProfileId: "a",
            hiddenIds: new HashSet<string>());

        Assert.False(set.Hidden.Single(r => r.Id == "a").IsDefault);
        Assert.True(set.Visible.Single(r => r.Id == "b").IsDefault);
    }
}
