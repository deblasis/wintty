using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ghostty.Core.Tabs;

/// <summary>
/// A named, colored set of tabs. Identity is the <see cref="Id"/>;
/// membership lives on <see cref="TabModel.Group"/> and the registry on
/// <see cref="TabManager.Groups"/>, so a group with no members is an
/// orphan the manager dissolves rather than state in its own right.
/// Groups are never pinned: pinning a member removes it from the group,
/// so a run always sits in the unpinned zone.
///
/// Title, Color, and IsCollapsed raise <see cref="PropertyChanged"/>:
/// the registry has no collection event of its own, so the group itself
/// is the carrier. Membership is NOT carried here: it rides
/// <see cref="TabModel.Group"/>'s own notification, and a dissolved
/// group raises nothing because the strips re-read the projection that
/// no longer names it.
/// </summary>
internal sealed class TabGroup : INotifyPropertyChanged
{
    public Guid Id { get; }

    public TabGroup() => Id = Guid.NewGuid();

    /// <summary>
    /// Session restore's ctor: group identity survives restart, so the
    /// saved id comes back AS the live one (<see
    /// cref="TabManager.RestoreGroup"/>). Fresh groups mint their own.
    /// </summary>
    internal TabGroup(Guid id) => Id = id;

    /// <summary>Label shown on the vertical header row / horizontal chip.</summary>
    public string Title
    {
        get => field;
        set { if (field != value) { field = value; Raise(); } }
    } = "New group";

    /// <summary>
    /// Swatch shared with the per-tab tint palette: a group has no "no
    /// color" state, so the default is the palette's first swatch.
    ///
    /// The setter enforces that rather than trusting its writers, because
    /// the writers cannot all be checked: the colour picker renders
    /// <c>PaletteRows</c>, which leads with <see cref="TabColor.None"/>, and
    /// it is opened for a group from two places; a restored session replays
    /// whatever the last run persisted. The paint sites downstream index the
    /// palette with no guard -- chip swatch and ink, run label, vertical
    /// header row, and the switcher's card, ring and dot -- so a None
    /// arriving here surfaced as a KeyNotFoundException in the middle of a
    /// paint pass instead of at the point that wrote it.
    /// </summary>
    public TabColor Color
    {
        get => field;
        set
        {
            var resolved = TabColorPalette.EnsureGroupColor(value);
            if (field != resolved) { field = resolved; Raise(); }
        }
    } = TabColorPalette.DefaultGroupColor;

    /// <summary>
    /// One bit shared by both strip modes, so a layout switch mid-collapse
    /// needs no repair. Purely presentational: it never mutates the tab
    /// list and never touches activation.
    /// </summary>
    public bool IsCollapsed
    {
        get => field;
        set { if (field != value) { field = value; Raise(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
