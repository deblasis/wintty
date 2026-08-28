using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Session;

/// <summary>Top-level persisted session. Version guards format changes.</summary>
internal sealed class SessionState
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// True only when written by the clean app-shutdown path. Mid-session
    /// (debounced) writes set this false so <c>window-save-state=default</c>
    /// does not restore after a crash. <c>always</c> ignores this flag.
    /// </summary>
    public bool CleanShutdown { get; set; }

    public List<WindowSession> Windows { get; set; } = [];
}

/// <summary>One non-quake window: geometry + ordered tabs + active tab.</summary>
internal sealed class WindowSession
{
    public WindowGeometry Geometry { get; set; } = new();
    public int ActiveTabIndex { get; set; }
    public List<TabSession> Tabs { get; set; } = [];

    /// <summary>
    /// Groups referenced by this window's tabs. Members point at their
    /// group by <see cref="TabSession.GroupId"/>; a tab whose id has no
    /// entry here restores ungrouped, and the restore-side repair that
    /// enforces that is the later PR that wires restore.
    /// </summary>
    public List<GroupSession> Groups { get; set; } = [];
}

/// <summary>Mirror of the geometry fields already saved by WindowState.</summary>
internal sealed class WindowGeometry
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// One tab: the profile it was opened with, an optional user title
/// override, the split tree, and the paths of the active/zoomed leaves.
/// </summary>
internal sealed class TabSession
{
    public string? ProfileId { get; set; }
    public string? UserTitle { get; set; }
    public PaneNodeDto Tree { get; set; } = null!;

    /// <summary>Path to the focused leaf (empty = root leaf).</summary>
    public bool[] ActiveLeafPath { get; set; } = [];

    /// <summary>Path to the zoomed leaf, or null if no pane was zoomed.</summary>
    public bool[]? ZoomedLeafPath { get; set; }

    /// <summary>True when the tab lived in the pinned prefix.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Id of the tab's group, or null when ungrouped.</summary>
    public Guid? GroupId { get; set; }
}

/// <summary>
/// Identity and presentation state of one tab group; members reference it
/// by id. Group identity survives restart, unlike per-tab color, which
/// stays in-memory by prior decision.
/// </summary>
internal sealed class GroupSession
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public TabColor Color { get; set; }
    public bool Collapsed { get; set; }
}

/// <summary>Polymorphic split-tree node. Leaf or split.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LeafDto), "leaf")]
[JsonDerivedType(typeof(SplitDto), "split")]
internal abstract class PaneNodeDto { }

internal sealed class LeafDto : PaneNodeDto
{
    /// <summary>Profile id to re-resolve at restore; null = legacy/no-profile leaf.</summary>
    public string? ProfileId { get; set; }

    /// <summary>Replayed verbatim if <see cref="ProfileId"/> no longer resolves.</summary>
    public LeafCommand? Fallback { get; set; }

    /// <summary>
    /// The pane's last-reported directory (OSC 7), or null when the shell
    /// never reported one. The resolver spawns the rebuilt pane here in
    /// preference to the profile's static working-directory.
    /// </summary>
    public string? Cwd { get; set; }
}

internal sealed class SplitDto : PaneNodeDto
{
    public PaneOrientation Orientation { get; set; }
    public double Ratio { get; set; } = 0.5;
    public PaneNodeDto Child1 { get; set; } = null!;
    public PaneNodeDto Child2 { get; set; } = null!;
}

/// <summary>Minimal command snapshot to spawn a shell without a profile.</summary>
internal sealed class LeafCommand
{
    public string ResolvedCommand { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// Source-generated, trim/AOT-safe serialization context (mirrors
/// WindowStateContext). Reflection STJ would drop properties under the
/// trimmer.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionState))]
internal partial class SessionJsonContext : JsonSerializerContext
{
}
