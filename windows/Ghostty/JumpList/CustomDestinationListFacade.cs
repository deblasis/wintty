using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ghostty.Core.JumpList;
using Ghostty.Interop;
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
/// </summary>
internal sealed class CustomDestinationListFacade : ICustomDestinationListFacade
{
    private readonly ICustomDestinationList _list;
    // AddUserTasks on ICustomDestinationList is a one-shot call per
    // BeginList/Commit cycle - it creates the Tasks slot in the shell
    // store and a second invocation returns 0x800700B7 ALREADY_EXISTS.
    // So we buffer every AddTask call and flush a single AddUserTasks
    // in Commit.
    private readonly List<(string exe, string args, string title)> _pendingTasks = new();

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
    {
        var collection = CreateCollection();
        foreach (var e in entries)
        {
            var link = CreateShellLink(e.exePath, e.args, e.title);
            AddObjectToCollection(collection, link);
        }
        // The IObjectCollection IUnknown is also queryable as
        // IObjectArray; cross-interface QI happens through the
        // shared ComWrappers strategy.
        var array = QueryAsObjectArray(collection);
        _list.AppendCategory(categoryName, array);
    }

    public void Commit()
    {
        if (_pendingTasks.Count > 0)
        {
            var collection = CreateCollection();
            foreach (var t in _pendingTasks)
            {
                var link = CreateShellLink(t.exe, t.args, t.title);
                AddObjectToCollection(collection, link);
            }
            _list.AddUserTasks(QueryAsObjectArray(collection));
            _pendingTasks.Clear();
        }
        _list.CommitList();
    }

    private static IShellLinkW CreateShellLink(string exePath, string arguments, string title)
    {
        var link = ShellLink.CreateInstance<IShellLinkW>();
        link.SetPath(exePath);
        link.SetArguments(arguments);
        link.SetDescription(title);
        // Title shown in the jump list comes from System.Title, not
        // the description. Set it via the IPropertyStore side of the
        // same object.
        ShellLinkTitleHelper.SetTitle(link, title);
        return link;
    }

    private static IObjectCollection CreateCollection()
        => EnumerableObjectCollection.CreateInstance<IObjectCollection>();

    /// <summary>
    /// AddObject takes a raw IUnknown* under [GeneratedComInterface]
    /// (the runtime-marshalled <c>UnmanagedType.IUnknown</c> path is
    /// trim-unsafe). The CsWin32 generated signature wraps this
    /// behind <c>[MarshalAs(UnmanagedType.Interface)] object punk</c>
    /// where the COM source generator handles QI internally, so we
    /// can pass the typed RCW directly.
    /// </summary>
    private static void AddObjectToCollection(IObjectCollection collection, object com)
    {
        collection.AddObject(com);
    }

    /// <summary>
    /// QueryInterface the IObjectCollection's underlying IUnknown
    /// for IObjectArray and return the typed wrapper. The shared
    /// ComWrappers strategy hands out a wrapper that implements both
    /// interfaces against the same RCW identity.
    /// </summary>
    private static IObjectArray QueryAsObjectArray(IObjectCollection collection)
    {
        var unknown = ComCreate.GetComInterfaceForObject(collection);
        try
        {
            return (IObjectArray)ComCreate.Wrap(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
