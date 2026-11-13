using System;
using System.Collections.Generic;

namespace Ghostty.Core.Ssh;

/// <summary>One row of a process snapshot.</summary>
public readonly record struct ProcessEntry(uint Pid, uint ParentPid, string ExeBasename);

/// <summary>
/// Finds the ssh client a pane is running, given a process snapshot and
/// the pane's shell pid. Pure selection logic; the snapshot itself is
/// taken by the caller.
/// </summary>
public static class SshProcessFinder
{
    /// <summary>
    /// The pid of the outermost <c>ssh.exe</c> at or below
    /// <paramref name="rootPid"/>, or null. Outermost because a jump host
    /// (<c>-J</c>, ProxyCommand) runs a second ssh as a child of the one
    /// the user started, and the user's is the one that names the target.
    /// </summary>
    public static uint? FindSsh(IReadOnlyList<ProcessEntry> snapshot, uint rootPid)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var byParent = new Dictionary<uint, List<ProcessEntry>>();
        ProcessEntry? root = null;
        foreach (var entry in snapshot)
        {
            if (entry.Pid == rootPid) root = entry;
            if (!byParent.TryGetValue(entry.ParentPid, out var list))
                byParent[entry.ParentPid] = list = new List<ProcessEntry>();
            list.Add(entry);
        }

        if (root is { } r && IsSsh(r.ExeBasename)) return rootPid;

        // Breadth-first, so the first ssh found is the shallowest. The
        // visited set guards against pid reuse forming a cycle.
        var visited = new HashSet<uint> { rootPid };
        var queue = new Queue<uint>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            if (!byParent.TryGetValue(queue.Dequeue(), out var children)) continue;
            foreach (var child in children)
            {
                if (!visited.Add(child.Pid)) continue;
                if (IsSsh(child.ExeBasename)) return child.Pid;
                queue.Enqueue(child.Pid);
            }
        }
        return null;
    }

    private static bool IsSsh(string exe)
        => exe.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase)
        || exe.Equals("ssh", StringComparison.OrdinalIgnoreCase);
}
