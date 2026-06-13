using System;
using System.Collections.Generic;
using Ghostty.Core.Panes;

namespace Ghostty.Core.Session;

/// <summary>
/// Pure conversion between the live <see cref="PaneNode"/> tree and the
/// serializable <see cref="PaneNodeDto"/>, plus leaf-identity paths
/// (Child1/Child2 step lists) for the active/zoomed leaves. No UI and no
/// profile resolution -- the caller supplies a leaf factory so the app
/// layer owns re-resolving a profile id into a
/// <see cref="Ghostty.Core.Profiles.ProfileSnapshot"/>.
/// </summary>
internal static class SessionTree
{
    /// <summary>Snapshot the tree structure, recording each leaf's profile id and a fallback command.</summary>
    public static PaneNodeDto CaptureTree(PaneNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        switch (root)
        {
            case SplitPane s:
                return new SplitDto
                {
                    Orientation = s.Orientation,
                    Ratio = s.Ratio,
                    Child1 = CaptureTree(s.Child1),
                    Child2 = CaptureTree(s.Child2),
                };
            case LeafPane leaf:
                var snap = leaf.Snapshot;
                return new LeafDto
                {
                    ProfileId = snap?.ProfileId,
                    Fallback = snap is null
                        ? null
                        : new LeafCommand
                        {
                            ResolvedCommand = snap.ResolvedCommand,
                            WorkingDirectory = snap.WorkingDirectory,
                            DisplayName = snap.DisplayName,
                        },
                };
            default:
                throw new ArgumentException($"Unknown PaneNode type: {root.GetType()}");
        }
    }

    /// <summary>
    /// Rebuild a <see cref="PaneNode"/> tree from a dto. <paramref name="makeLeaf"/>
    /// turns a <see cref="LeafDto"/> into a <see cref="LeafPane"/> (the app
    /// layer re-resolves the profile and sets <see cref="LeafPane.Snapshot"/>;
    /// <see cref="LeafPane.Tag"/> is left null for the host to populate).
    /// </summary>
    public static PaneNode RebuildTree(PaneNodeDto dto, Func<LeafDto, LeafPane> makeLeaf)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(makeLeaf);
        return dto switch
        {
            SplitDto s => new SplitPane(
                s.Orientation,
                RebuildTree(s.Child1, makeLeaf),
                RebuildTree(s.Child2, makeLeaf),
                Math.Clamp(s.Ratio, SplitPane.MinRatio, SplitPane.MaxRatio)),
            LeafDto l => makeLeaf(l),
            _ => throw new ArgumentException($"Unknown PaneNodeDto type: {dto.GetType()}"),
        };
    }

    /// <summary>Path of Child1(false)/Child2(true) steps from root to target; empty if target is root.</summary>
    public static bool[] PathOf(PaneNode root, PaneNode target)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(target);
        var steps = new List<bool>();
        return Walk(root, target, steps) ? steps.ToArray() : Array.Empty<bool>();

        static bool Walk(PaneNode node, PaneNode target, List<bool> acc)
        {
            if (ReferenceEquals(node, target)) return true;
            if (node is not SplitPane s) return false;
            acc.Add(false);
            if (Walk(s.Child1, target, acc)) return true;
            acc[^1] = true;
            if (Walk(s.Child2, target, acc)) return true;
            acc.RemoveAt(acc.Count - 1);
            return false;
        }
    }

    /// <summary>Resolve a path back to a node, or null if the path does not fit the tree.</summary>
    public static PaneNode? Resolve(PaneNode root, IReadOnlyList<bool> path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        var node = root;
        foreach (var goChild2 in path)
        {
            if (node is not SplitPane s) return null;
            node = goChild2 ? s.Child2 : s.Child1;
        }
        return node;
    }
}
