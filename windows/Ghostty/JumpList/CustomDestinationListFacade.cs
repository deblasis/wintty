using System;
using System.Collections.Generic;
using Ghostty.Core.JumpList;
using Windows.Win32;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Ghostty.JumpList;

/// <summary>
/// Real implementation of <see cref="ICustomDestinationListFacade"/>.
/// Creates the underlying <see cref="ICustomDestinationList"/> COM
/// object via the CsWin32-generated DestinationList coclass factory
/// and forwards every facade call to it.
///
/// One instance per process is typical; <see cref="JumpListBuilder"/>
/// calls <see cref="BeginList"/> and <see cref="Commit"/> in pairs
/// to replace the list wholesale.
///
/// The shell links and the IObjectArray that carries them are built by
/// <see cref="ShellLinkCollection"/> in Ghostty.Core, where tests can
/// run that code against real shell COM objects.
/// </summary>
internal sealed class CustomDestinationListFacade : ICustomDestinationListFacade
{
    private readonly ICustomDestinationList _list;
    // AddUserTasks on ICustomDestinationList is a one-shot call per
    // BeginList/Commit cycle - it creates the Tasks slot in the shell
    // store and a second invocation returns 0x800700B7 ALREADY_EXISTS.
    // So we buffer every AddTask call and flush a single AddUserTasks
    // in Commit.
    private readonly List<(string exePath, string args, string title)> _pendingTasks = new();

    public CustomDestinationListFacade()
    {
        _list = DestinationList.CreateInstance<ICustomDestinationList>();
    }

    public void SetAppId(string appId) => _list.SetAppID(appId);

    public uint BeginList()
    {
        // Friendly extension overload accepts `in Guid` and pins
        // internally; we just discard the removed-destinations
        // IObjectArray (the existing facade does the same).
        var iid = typeof(IObjectArray).GUID;
        _list.BeginList(out var maxSlots, in iid, out _);
        return maxSlots;
    }

    public void AddTask(string exePath, string arguments, string title)
        => _pendingTasks.Add((exePath, arguments, title));

    public void AddCategory(string categoryName, IReadOnlyList<(string exePath, string args, string title)> entries)
        => _list.AppendCategory(categoryName, ShellLinkCollection.Build(entries, LogSkipped));

    public void Commit()
    {
        if (_pendingTasks.Count > 0)
        {
            _list.AddUserTasks(ShellLinkCollection.Build(_pendingTasks, LogSkipped));
            _pendingTasks.Clear();
        }
        _list.CommitList();
    }

    private static void LogSkipped(Exception error, string exePath, string arguments, string title)
        => Ghostty.Logging.StaticLoggers.App.LogJumpListItemSkipped(error, title, arguments, exePath);
}
