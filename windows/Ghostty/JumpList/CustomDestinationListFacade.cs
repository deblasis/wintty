using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ghostty.Core.JumpList;
using Ghostty.Interop;

namespace Ghostty.JumpList;

/// <summary>
/// Real implementation of <see cref="ICustomDestinationListFacade"/>.
/// Creates the underlying <see cref="ShellInterop.ICustomDestinationList"/>
/// COM object via <see cref="ShellInterop.CoCreateInstance"/> on
/// construction and forwards every facade call to it.
///
/// One instance per process is typical; <see cref="JumpListBuilder"/>
/// calls <see cref="BeginList"/> and <see cref="Commit"/> in pairs
/// to replace the list wholesale.
/// </summary>
internal sealed class CustomDestinationListFacade : ICustomDestinationListFacade
{
    private readonly ShellInterop.ICustomDestinationList _list;
    // AddUserTasks on ICustomDestinationList is a one-shot call per
    // BeginList/Commit cycle — it creates the Tasks slot in the shell
    // store and a second invocation returns 0x800700B7 ALREADY_EXISTS.
    // So we buffer every AddTask call and flush a single AddUserTasks
    // in Commit.
    private readonly List<(string exe, string args, string title)> _pendingTasks = new();

    public CustomDestinationListFacade()
    {
        _list = (ShellInterop.ICustomDestinationList)ComCreate.Create(
            ShellInterop.CLSID_DestinationList,
            ShellInterop.IID_ICustomDestinationList);
    }

    public void SetAppId(string appId) => _list.SetAppID(appId);

    public uint BeginList()
    {
        var iid = ShellInterop.IID_IObjectArray;
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

    private static ShellInterop.IShellLinkW CreateShellLink(string exePath, string arguments, string title)
    {
        var link = (ShellInterop.IShellLinkW)ComCreate.Create(
            ShellInterop.CLSID_ShellLink,
            ShellInterop.IID_IShellLinkW);
        link.SetPath(exePath);
        link.SetArguments(arguments);
        link.SetDescription(title);
        // Title shown in the jump list comes from System.Title, not
        // the description. Set it via the IPropertyStore side of the
        // same object.
        ShellInterop.SetShellLinkTitle(link, title);
        return link;
    }

    private static ShellInterop.IObjectCollection CreateCollection()
        => (ShellInterop.IObjectCollection)ComCreate.Create(
            ShellInterop.CLSID_EnumerableObjectCollection,
            ShellInterop.IID_IObjectCollection);

    /// <summary>
    /// AddObject takes a raw IUnknown* under [GeneratedComInterface]
    /// (the runtime-marshalled <c>UnmanagedType.IUnknown</c> path is
    /// trim-unsafe). Bridge by calling Marshal.GetIUnknownForObject
    /// for the duration of the call and releasing the reference
    /// once the collection has AddRef'd it.
    /// </summary>
    private static void AddObjectToCollection(ShellInterop.IObjectCollection collection, object com)
    {
        var unknown = ComCreate.GetIUnknown(com);
        try
        {
            collection.AddObject(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// QueryInterface the IObjectCollection's underlying IUnknown
    /// for IObjectArray and return the typed wrapper. The shared
    /// ComWrappers strategy hands out a wrapper that implements both
    /// interfaces against the same RCW identity.
    /// </summary>
    private static ShellInterop.IObjectArray QueryAsObjectArray(ShellInterop.IObjectCollection collection)
    {
        var unknown = ComCreate.GetIUnknown(collection);
        try
        {
            return (ShellInterop.IObjectArray)ComCreate.Wrap(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
