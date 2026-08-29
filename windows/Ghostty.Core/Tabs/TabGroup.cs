using System;

namespace Ghostty.Core.Tabs;

/// <summary>
/// A named, colored set of tabs. Identity is the <see cref="Id"/>;
/// membership lives on <see cref="TabModel.Group"/> and the registry on
/// <see cref="TabManager.Groups"/>, so a group with no members is an
/// orphan the manager dissolves rather than state in its own right.
///
/// Groups are never pinned: pinning a member removes it from the group
/// (the Chrome rule <see cref="TabManager.SetPinned"/> applies), so a
/// run always sits in the unpinned zone.
///
/// Plain get/set properties on purpose: the model invariants read these
/// directly and mutate them through the manager, and the strips that
/// will render title, color, and collapse state arrive in later PRs
/// with whatever change plumbing they need at that point.
/// </summary>
internal sealed class TabGroup
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
    public string Title { get; set; } = "New group";

    /// <summary>
    /// Swatch shared with the per-tab tint palette. Unlike a tab, a group
    /// has no "no color" state to render (the rail and the chip are its
    /// identity), so the default is the palette's first swatch rather
    /// than <see cref="TabColor.None"/>.
    /// </summary>
    public TabColor Color { get; set; } = TabColor.Blue;

    /// <summary>
    /// One bit shared by both strip modes, so a layout switch mid-collapse
    /// needs no repair. Purely presentational: collapsing never mutates
    /// the tab list and never touches activation.
    /// </summary>
    public bool IsCollapsed { get; set; }
}
