using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The change notification itself. A shell drives the title and directory
/// setters at every prompt, on every tab, and each raise used to mint a
/// fresh <see cref="PropertyChangedEventArgs"/>; the names are a closed set
/// known at compile time, so one instance per name is enough forever.
///
/// Caching them trades an allocation for a hazard: a hand-written constant
/// can name the wrong property, which <c>[CallerMemberName]</c> made
/// impossible. The second test here is the guard against that.
/// </summary>
public class TabChangeArgsTests
{
    private static List<PropertyChangedEventArgs> Record(INotifyPropertyChanged source)
    {
        var seen = new List<PropertyChangedEventArgs>();
        source.PropertyChanged += (_, e) => seen.Add(e);
        return seen;
    }

    // --- one instance per name, not one per raise ---

    /// <summary>
    /// Across two MODELS, not two raises on one. A per-instance cache would
    /// satisfy the same-instance check while still allocating a set per tab,
    /// which is the cost the change exists to remove -- a window holds as
    /// many tabs as the user opens.
    /// </summary>
    [Fact]
    public void RaisingTheSameProperty_ReusesOneArgsObject_AcrossEveryTab()
    {
        var one = new TabModel(new FakePaneHost());
        var other = new TabModel(new FakePaneHost());
        var seenOne = Record(one);
        var seenOther = Record(other);

        one.IsPinned = true;
        one.IsPinned = false;
        other.IsPinned = true;

        var pinned = seenOne.Where(e => e.PropertyName == nameof(TabModel.IsPinned)).ToList();
        Assert.Equal(2, pinned.Count);
        Assert.Same(pinned[0], pinned[1]);
        Assert.Same(pinned[0], Assert.Single(seenOther,
            e => e.PropertyName == nameof(TabModel.IsPinned)));
    }

    [Fact]
    public void TheDerivedRaises_ReuseOneArgsObjectEach()
    {
        var tab = new TabModel(new FakePaneHost());
        var seen = Record(tab);

        tab.ShellReportedCwd = @"C:\one";
        tab.ShellReportedCwd = @"C:\two";

        // Every name the directory setter fans out to, not just its own.
        foreach (var name in new[]
        {
            nameof(TabModel.ShellReportedCwd),
            nameof(TabModel.EffectiveTitle),
            nameof(TabModel.IsHome),
            nameof(TabModel.WordTitle),
            nameof(TabModel.TooltipText),
            nameof(TabModel.HoverText),
        })
        {
            var raises = seen.Where(e => e.PropertyName == name).ToList();
            Assert.Equal(2, raises.Count);
            Assert.Same(raises[0], raises[1]);
        }
    }

    [Fact]
    public void TheIconViewModel_ReusesOneArgsObject()
    {
        var vm = new TabIconViewModel(new IconSpec.BundledKey("pwsh"), "pwsh");
        var seen = Record(vm);

        vm.SetOverride(new IconSpec.Mdl2Token(0xE700), "one");
        vm.RevertToProfile();
        vm.SetOverride(new IconSpec.Mdl2Token(0xE700), "two");

        var icon = seen.Where(e => e.PropertyName == nameof(TabIconViewModel.Icon)).ToList();
        Assert.True(icon.Count >= 2);
        Assert.Same(icon[0], icon[1]);
    }

    [Fact]
    public void TheGroup_ReusesOneArgsObject()
    {
        var group = new TabGroup();
        var seen = Record(group);

        group.Title = "one";
        group.Title = "two";

        var title = seen.Where(e => e.PropertyName == nameof(TabGroup.Title)).ToList();
        Assert.Equal(2, title.Count);
        Assert.Same(title[0], title[1]);
    }

    // --- a cached constant must still name its own property ---

    /// <summary>
    /// Every settable property that notifies at all must name itself. This
    /// is what <c>[CallerMemberName]</c> used to guarantee for free: a
    /// hand-written <c>Args.IsPinned</c> in the <c>IsIdle</c> setter would
    /// notify the wrong listeners and nothing else would catch it.
    /// </summary>
    /// <summary>
    /// The properties that deliberately notify nothing. Named rather than
    /// inferred: a walk that simply skipped whatever stayed silent could not
    /// tell a plain auto-property from one whose <c>Raise</c> was deleted,
    /// and would pass on both.
    /// </summary>
    private static readonly string[] SilentProperties = [nameof(TabModel.ProfileId)];

    [Fact]
    public void EverySettableProperty_RaisesItsOwnName()
    {
        var properties = typeof(TabModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetSetMethod() is not null)
            .ToList();

        // A guard on the guard: if the reflection filter ever silently
        // matches nothing, the test would pass while proving nothing.
        Assert.True(properties.Count >= 10, $"expected the model's settable properties, found {properties.Count}");

        foreach (var property in properties)
        {
            var value = ValueFor(property.PropertyType);
            Assert.True(value is not null,
                $"{property.Name} is settable but this walk cannot build a value for "
                + $"{property.PropertyType.Name}, so it is silently uncovered");

            var tab = new TabModel(new FakePaneHost());
            var seen = Record(tab);
            property.SetValue(tab, value);

            if (SilentProperties.Contains(property.Name))
            {
                Assert.Empty(seen);
                continue;
            }
            Assert.Contains(property.Name, seen.Select(e => e.PropertyName));
        }
    }

    /// <summary>
    /// The one notifying property the public-setter walk cannot reach: its
    /// setter is private, and it is the property carrying the strip's
    /// starting state.
    /// </summary>
    [Fact]
    public void TheStartingFlag_RaisesItsOwnName()
    {
        var tab = new TabModel(new FakePaneHost());
        var seen = Record(tab);

        tab.BeginSettling();

        Assert.Contains(nameof(TabModel.IsSettling), seen.Select(e => e.PropertyName));
    }

    /// <summary>
    /// The derived fan-out raises five names, and every one of them is a
    /// hand-written constant. A swap inside it -- WordTitle where
    /// EffectiveTitle belongs -- leaves both names present, so a test that
    /// only asked "are they all there" would pass. Each is raised exactly
    /// once per change.
    /// </summary>
    [Fact]
    public void TheDerivedFanOut_RaisesEachNameExactlyOnce()
    {
        var tab = new TabModel(new FakePaneHost());
        var seen = Record(tab);

        tab.ShellReportedCwd = @"C:\one";

        foreach (var name in new[]
        {
            nameof(TabModel.ShellReportedCwd),
            nameof(TabModel.EffectiveTitle),
            nameof(TabModel.IsHome),
            nameof(TabModel.WordTitle),
            nameof(TabModel.TooltipText),
            nameof(TabModel.HoverText),
        })
        {
            Assert.Single(seen, e => e.PropertyName == name);
        }
        Assert.Equal(6, seen.Count);
    }

    /// <summary>
    /// The other two classes that took the same treatment. Their setters
    /// name their constants by hand exactly as the model's do, and nothing
    /// else walks them.
    /// </summary>
    [Fact]
    public void TheGroupsProperties_EachRaiseTheirOwnName()
    {
        var group = new TabGroup();
        var seen = Record(group);

        group.Title = "build";
        group.Color = TabColor.Green;
        group.IsCollapsed = true;

        Assert.Equal(
            [nameof(TabGroup.Title), nameof(TabGroup.Color), nameof(TabGroup.IsCollapsed)],
            seen.Select(e => e.PropertyName));
    }

    [Fact]
    public void TheIconViewModels_Properties_EachRaiseTheirOwnName()
    {
        var vm = new TabIconViewModel(new IconSpec.BundledKey("pwsh"), "pwsh");
        var seen = Record(vm);

        vm.SetOverride(new IconSpec.Mdl2Token(0xE700), "vim");

        // The icon change carries its two derived readings with it, and the
        // tooltip moved on its own.
        Assert.Equal(
            [
                nameof(TabIconViewModel.Icon),
                nameof(TabIconViewModel.IsMdl2Glyph),
                nameof(TabIconViewModel.Mdl2CodePoint),
                nameof(TabIconViewModel.TooltipText),
            ],
            seen.Select(e => e.PropertyName));
    }

    private static object? ValueFor(Type type)
    {
        if (type == typeof(string)) return "changed";
        if (type == typeof(bool)) return true;
        if (type == typeof(TabProgressState)) return TabProgressState.Indeterminate;
        if (type == typeof(TabGroup)) return new TabGroup();
        // Any non-default member will do: the point is that the setter runs
        // and notifies, not which value it lands on.
        if (type.IsEnum) return Enum.ToObject(type, 1);
        return null;
    }
}
