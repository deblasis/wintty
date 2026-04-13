using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Ghostty.Interop;

/// <summary>
/// Thin wrapper around a single process-wide
/// <see cref="StrategyBasedComWrappers"/> instance. Used for
/// cross-interface QI across <c>[GeneratedComInterface]</c>
/// types - for example queueing a jump-list <c>IShellLinkW</c> up
/// for its <c>IPropertyStore</c> facet, where
/// <c>Marshal.GetIUnknownForObject</c> is a runtime-only SYSLIB1099
/// against generated COM types.
///
/// CoCreateInstance callers migrated to CsWin32 coclass factories
/// (DestinationList, ShellLink, EnumerableObjectCollection,
/// TaskbarList) and no longer need <c>ComCreate.Create</c>; only
/// <see cref="Wrap"/> and <see cref="GetComInterfaceForObject"/>
/// survive.
/// </summary>
internal static class ComCreate
{
    /// <summary>
    /// Shared ComWrappers instance. Strategy-based wrappers handle
    /// QueryInterface dispatch across every <c>[GeneratedComInterface]</c>
    /// the runtime knows about, so a single instance is enough for
    /// the whole process.
    /// </summary>
    private static readonly StrategyBasedComWrappers s_wrappers = new();

    /// <summary>
    /// Wraps a raw COM interface pointer with the shared
    /// <see cref="StrategyBasedComWrappers"/> strategy and returns
    /// an RCW.
    /// </summary>
    /// <remarks>
    /// OWNERSHIP CONTRACT: the caller retains ownership of
    /// <paramref name="unknown"/>. This method AddRefs internally
    /// via GetOrCreateObjectForComInstance, so the caller MUST
    /// still Release the original pointer when finished. Do not
    /// pass ownership in.
    /// </remarks>
    public static object Wrap(IntPtr unknown)
        => s_wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.None);

    /// <summary>
    /// Returns a raw COM interface pointer for <paramref name="com"/>
    /// suitable for passing to Marshal.QueryInterface or re-wrapping
    /// via <see cref="Wrap"/>. The returned pointer is whatever facet
    /// the object's ComputeVtables override produced - at slot 0 it
    /// dispatches QueryInterface so any COM facet reachable via QI
    /// works after a subsequent Wrap call. Caller MUST Release the
    /// returned pointer when finished.
    /// </summary>
    /// <remarks>
    /// Replacement for the runtime-only
    /// <see cref="Marshal.GetIUnknownForObject"/>, which warns
    /// SYSLIB1099 against <c>[GeneratedComInterface]</c> targets.
    /// Renamed from <c>GetIUnknown</c>: the old name implied slot-0
    /// IUnknown specifically, but what
    /// <see cref="StrategyBasedComWrappers.GetOrCreateComInterfaceForObject"/>
    /// actually returns is whatever facet the object's
    /// <c>ComputeVtables</c> override produced. It still works for
    /// cross-interface QI because every COM interface dispatches
    /// QueryInterface at slot 0.
    /// </remarks>
    public static IntPtr GetComInterfaceForObject(object com)
        => s_wrappers.GetOrCreateComInterfaceForObject(com, CreateComInterfaceFlags.None);
}
