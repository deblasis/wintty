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

    public uint BeginList()
    {
        BeginListCalls++;
        return 10; // max slots; arbitrary for fake
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
