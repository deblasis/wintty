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

    [Fact]
    public void RaisingTheSameProperty_ReusesOneArgsObject()
    {
        var tab = new TabModel(new FakePaneHost());
        var seen = Record(tab);

        tab.IsPinned = true;
        tab.IsPinned = false;

        var pinned = seen.Where(e => e.PropertyName == nameof(TabModel.IsPinned)).ToList();
        Assert.Equal(2, pinned.Count);
        Assert.Same(pinned[0], pinned[1]);
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

        var checkedAtLeastOne = false;
        foreach (var property in properties)
        {
            if (ValueFor(property.PropertyType) is not { } value) continue;

            var tab = new TabModel(new FakePaneHost());
            var seen = Record(tab);
            property.SetValue(tab, value);

            // A property that notifies nothing (a plain auto-property) is
            // fine; one that notifies must include its own name.
            if (seen.Count == 0) continue;
            checkedAtLeastOne = true;
            Assert.Contains(property.Name, seen.Select(e => e.PropertyName));
        }

        Assert.True(checkedAtLeastOne, "no settable property notified; the reflection walk proved nothing");
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
