using System;
using Ghostty.Commands;
using Xunit;

namespace Ghostty.Tests.Commands;

public class FrecencyStoreTests
{
    [Fact]
    public void RecordUse_NewEntry_CreatesWithCountOne()
    {
        var store = new FrecencyStore();
        store.RecordUse("reset");
        Assert.Equal(1, store.Entries["reset"].UseCount);
    }

    [Fact]
    public void RecordUse_ExistingEntry_IncrementsCount()
    {
        var store = new FrecencyStore();
        store.RecordUse("reset");
        store.RecordUse("reset");
        Assert.Equal(2, store.Entries["reset"].UseCount);
    }

    [Fact]
    public void Score_NeverUsed_ReturnsZero()
    {
        var store = new FrecencyStore();
        Assert.Equal(0.0, store.Score("unknown"));
    }

    [Fact]
    public void Score_UsedToday_ReturnsUseCount()
    {
        var store = new FrecencyStore();
        store.RecordUse("reset");
        store.RecordUse("reset");
        store.RecordUse("reset");
        Assert.Equal(3.0, store.Score("reset"), 5);
    }

    [Fact]
    public void Score_UsedDaysAgo_Decays()
    {
        var store = new FrecencyStore();
        store.Entries["old"] = new FrecencyEntry
        {
            UseCount = 10,
            LastUsed = DateTime.UtcNow.AddDays(-7),
        };
        var score = store.Score("old");
        Assert.True(score > 4.0 && score < 5.0, $"Expected ~4.78, got {score}");
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var store = new FrecencyStore();
        store.RecordUse("reset");
        store.RecordUse("new_tab");

        var json = store.ToJson();
        var restored = FrecencyStore.FromJson(json);

        Assert.Equal(1, restored.Entries["reset"].UseCount);
        Assert.Equal(1, restored.Entries["new_tab"].UseCount);
    }

    [Fact]
    public void FromJson_InvalidJson_ReturnsEmpty()
    {
        var store = FrecencyStore.FromJson("not json");
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void FromJson_Null_ReturnsEmpty()
    {
        var store = FrecencyStore.FromJson(null);
        Assert.Empty(store.Entries);
    }
}
