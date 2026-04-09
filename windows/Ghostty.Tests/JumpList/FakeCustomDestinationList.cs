using System.Collections.Generic;
using Ghostty.Core.JumpList;

namespace Ghostty.Tests.JumpList;

/// <summary>
/// Recording fake for <see cref="ICustomDestinationListFacade"/>.
/// Stores every call in order so tests can assert sequences.
/// </summary>
internal sealed class FakeCustomDestinationList : ICustomDestinationListFacade
{
    public string? AppId { get; private set; }
    public int BeginListCalls { get; private set; }
    public int CommitCalls { get; private set; }
    public List<(string exe, string args, string title)> Tasks { get; } = new();
    public List<(string category, List<(string exe, string args, string title)> entries)> Categories { get; } = new();

    public void SetAppId(string appId) => AppId = appId;

    /// <summary>
    /// Max slots returned from <see cref="BeginList"/>. Defaults to
    /// <see cref="uint.MaxValue"/> ("no limit") so current tests don't
    /// accidentally exercise a clamping path that doesn't exist yet;
    /// future tests can override this to cover slot-limit behaviour.
    /// </summary>
    public uint MaxSlots { get; set; } = uint.MaxValue;

    public uint BeginList()
    {
        BeginListCalls++;
        return MaxSlots;
    }

    public void AddTask(string exePath, string arguments, string title)
        => Tasks.Add((exePath, arguments, title));

    public void AddCategory(string categoryName, IReadOnlyList<(string exePath, string args, string title)> entries)
    {
        var copy = new List<(string, string, string)>();
        foreach (var e in entries) copy.Add((e.exePath, e.args, e.title));
        Categories.Add((categoryName, copy));
    }

    public void Commit() => CommitCalls++;
}
