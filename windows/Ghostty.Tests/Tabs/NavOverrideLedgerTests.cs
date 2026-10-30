using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The add-once rule behind the vertical strip's NavigationView overrides
/// (deblasis/wintty#1413), run as behaviour rather than read as source. A
/// value in the dictionary that a deferred visual-state setter captured
/// must never be replaced or removed, so each key is added once and every
/// later write updates that same instance.
/// </summary>
public class NavOverrideLedgerTests
{
    // Every NavigationView key the vertical strip writes, by name: the rule
    // holds for each one, so a ledger that special-cased any of them fails
    // here. StripResourceGraphWiringTests checks this list against the keys
    // the strip's SetNavResource and ClearNavResource calls actually name,
    // both ways, so a new key in the strip has to be added here too.
    internal static readonly string[] Keys =
    {
        "NavigationViewCompactPaneBackground",
        "NavigationViewDefaultPaneBackground",
        "NavigationViewExpandedPaneBackground",
        "NavigationViewItemBackgroundPointerOver",
        "NavigationViewItemBackgroundPressed",
        "NavigationViewItemBackgroundSelected",
        "NavigationViewItemBackgroundSelectedPointerOver",
        "NavigationViewItemBackgroundSelectedPressed",
        "NavigationViewItemForeground",
        "NavigationViewItemForegroundPointerOver",
        "NavigationViewItemForegroundSelected",
        "NavigationViewItemForegroundSelectedPointerOver",
        "NavigationViewSelectionIndicatorForeground",
    };
    public static TheoryData<string> StripKeys
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var key in Keys) data.Add(key);
            return data;
        }
    }

    private sealed class Brush
    {
        public string Color;
        public Brush(string color) { Color = color; }
    }

    // A stand-in for NavView.Resources that records every add and refuses a
    // second add of a key, the way a replacement would show up.
    private sealed class FakeDictionary
    {
        public readonly Dictionary<string, Brush> Entries = new();
        public readonly List<string> Adds = new();
        public Exception? ThrowNext;
        public Exception? ThrowAfterAdd;

        public void Add(string key, Brush value)
        {
            if (ThrowNext is { } ex) { ThrowNext = null; throw ex; }
            Adds.Add(key);
            Entries[key] = value;
            if (ThrowAfterAdd is { } after) { ThrowAfterAdd = null; throw after; }
        }
    }

    private static (NavOverrideLedger<Brush> ledger, FakeDictionary dict) New()
    {
        var dict = new FakeDictionary();
        var ledger = new NavOverrideLedger<Brush>(
            dict.Add,
            ex => ex is InvalidOperationException,
            key => dict.Entries.TryGetValue(key, out var existing) ? existing : null);
        return (ledger, dict);
    }

    private static bool Write(NavOverrideLedger<Brush> ledger, string key, string color)
        => ledger.Write(key, () => new Brush(color), b => b.Color = color);

    [Theory]
    [MemberData(nameof(StripKeys))]
    public void ASecondWrite_KeepsTheSameInstance_AndChangesItsColour(string key)
    {
        var (ledger, dict) = New();

        Assert.True(Write(ledger, key, "#FF000001"));
        var first = dict.Entries[key];

        Assert.False(Write(ledger, key, "#FF000002"));
        Assert.False(Write(ledger, key, "#FF000003"));

        Assert.Same(first, dict.Entries[key]);
        Assert.Equal("#FF000003", first.Color);
        Assert.Equal(new[] { key }, dict.Adds);
    }

    [Theory]
    [MemberData(nameof(StripKeys))]
    public void AClear_PaintsTheThemeValue_IntoTheSameInstance_AndNeverAdds(string key)
    {
        var (ledger, dict) = New();
        Write(ledger, key, "#FF000001");
        var owned = dict.Entries[key];

        Assert.True(ledger.Clear(key, b => b.Color = "#THEME"));

        Assert.Same(owned, dict.Entries[key]);
        Assert.Equal("#THEME", owned.Color);
        Assert.Equal(new[] { key }, dict.Adds);
        Assert.True(ledger.Owns(key));

        // And a write after the clear is still in place.
        Assert.False(Write(ledger, key, "#FF000004"));
        Assert.Same(owned, dict.Entries[key]);
    }

    [Fact]
    public void AClear_OfAKeyNeverWritten_TouchesNothing()
    {
        var (ledger, dict) = New();
        var painted = false;

        Assert.False(ledger.Clear("NavigationViewItemForeground", _ => painted = true));

        Assert.False(painted);
        Assert.Empty(dict.Adds);
        Assert.False(ledger.Owns("NavigationViewItemForeground"));
    }

    [Fact]
    public void OnlyAnAdd_ReportsARereadNeeded_OncePerKey()
    {
        var (ledger, _) = New();
        var keys = Keys;

        var adds = 0;
        for (var pass = 0; pass < 5; pass++)
            foreach (var key in keys)
                if (Write(ledger, key, $"#FF00000{pass}")) adds++;

        Assert.Equal(keys.Length, adds);
    }

    [Fact]
    public void ATransientAddFailure_IsRetried_AndStillReportsTheAdd()
    {
        var (ledger, dict) = New();
        const string key = "NavigationViewItemBackgroundPointerOver";
        dict.ThrowNext = new InvalidOperationException("tearing down");

        Assert.True(Write(ledger, key, "#FF000001"));
        Assert.Empty(dict.Adds);
        Assert.False(ledger.Owns(key));

        Assert.True(Write(ledger, key, "#FF000002"));
        Assert.Equal(new[] { key }, dict.Adds);
        Assert.False(Write(ledger, key, "#FF000003"));
        Assert.Equal(new[] { key }, dict.Adds);
    }

    [Fact]
    public void AnAddThatCommitsThenThrows_IsAdopted_NotAddedAgain()
    {
        var (ledger, dict) = New();
        const string key = "NavigationViewItemBackgroundPointerOver";
        dict.ThrowAfterAdd = new InvalidOperationException("notification threw");

        // The add lands in the dictionary, then the notification throws
        // before the ledger can record it.
        Assert.True(Write(ledger, key, "#FF000001"));
        Assert.Equal(new[] { key }, dict.Adds);
        var landed = dict.Entries[key];

        // The next write adopts the entry that landed. It must not add a
        // second brush over a live key: that add is the replacement this
        // class exists to prevent.
        Assert.False(Write(ledger, key, "#FF000002"));
        Assert.True(ledger.Owns(key));
        Assert.Same(landed, dict.Entries[key]);
        Assert.Equal("#FF000002", landed.Color);
        Assert.Equal(new[] { key }, dict.Adds);
    }

    [Fact]
    public void ATransientReadFailure_ReportsAnAddAndRetries()
    {
        var dict = new FakeDictionary();
        var reads = 0;
        var ledger = new NavOverrideLedger<Brush>(
            dict.Add,
            ex => ex is InvalidOperationException,
            key =>
            {
                reads++;
                if (reads == 1) throw new InvalidOperationException("tearing down");
                return dict.Entries.TryGetValue(key, out var existing) ? existing : null;
            });
        const string key = "NavigationViewItemForeground";

        Assert.True(Write(ledger, key, "#FF000001"));
        Assert.Empty(dict.Adds);

        Assert.True(Write(ledger, key, "#FF000002"));
        Assert.Equal(new[] { key }, dict.Adds);
    }

    [Fact]
    public void AClear_ThatThrowsTransiently_DoesNotEscape()
    {
        var (ledger, _) = New();
        const string key = "NavigationViewItemForeground";
        Assert.True(Write(ledger, key, "#FF000001"));

        var ex = Record.Exception(
            () => ledger.Clear(key, _ => throw new InvalidOperationException("tearing down")));

        Assert.Null(ex);
        Assert.True(ledger.Owns(key));
    }

    [Fact]
    public void AnyOtherAddFailure_Propagates()
    {
        var (ledger, dict) = New();
        dict.ThrowNext = new ArgumentException("not transient");

        Assert.Throws<ArgumentException>(() => Write(ledger, "NavigationViewItemForeground", "#FF000001"));
    }

    [Fact]
    public void TheLedger_HasNoWayToRemoveOrReplaceAnEntry()
    {
        var methods = typeof(NavOverrideLedger<Brush>)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Clear", "Owns", "Write" }, methods);
    }

    // The theme sets the strip reads its own colours from. Expected values
    // are written out, not derived: they are XamlControlsResources' set
    // names, and High Contrast follows the OS, whatever the strip's theme.
    [Theory]
    [InlineData(true, false, new[] { "Light" })]
    [InlineData(false, false, new[] { "Dark", "Default" })]
    [InlineData(true, true, new[] { "HighContrast" })]
    [InlineData(false, true, new[] { "HighContrast" })]
    public void StripThemeKeys_PickTheSetXamlPicks(bool actualThemeIsLight, bool systemHighContrast, string[] expected)
        => Assert.Equal(expected, StripThemeKeys.For(actualThemeIsLight, systemHighContrast));
}
