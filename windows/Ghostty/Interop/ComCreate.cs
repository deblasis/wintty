using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Ghostty.Interop;

/// <summary>
/// Thin wrapper around a single process-wide
/// <see cref="StrategyBasedComWrappers"/> instance. The two helpers go
/// in opposite directions across the COM boundary:
///
/// - <see cref="Wrap"/> turns a raw interface pointer that native code
///   handed us (an out pointer, a callback argument) into a managed RCW.
/// - <see cref="GetComInterfaceForObject"/> builds a COM-callable
///   wrapper (CCW) so a MANAGED object that implements a
///   <c>[GeneratedComInterface]</c> type can be passed out to native
///   code. That is the only case a CCW is right for.
///
/// Neither is a way to QueryInterface an object you already hold. Never
/// pass an RCW (anything from CoCreateInstance, a CsWin32 coclass
/// factory such as ShellLink.CreateInstance, or <see cref="Wrap"/>) to
/// <see cref="GetComInterfaceForObject"/>: the CCW it builds answers
/// QueryInterface only for IUnknown, not for what the native object
/// behind the RCW supports, so re-wrapping it and casting to another
/// interface throws E_NOINTERFACE. To reach another interface of a
/// native object, cast the RCW itself: under
/// <c>[GeneratedComInterface]</c> the RCW implements
/// IDynamicInterfaceCastable and the cast performs a real
/// QueryInterface. When the target is a base interface (the generated
/// IObjectCollection derives from IObjectArray) it is just an upcast.
/// The jump list code once did both of those round-trips (IShellLinkW
/// to IPropertyStore, IObjectCollection to IObjectArray) and both threw;
/// see Ghostty.Core.JumpList.ShellLinkTitleHelper and ShellLinkCollection.
///
/// Nothing in the shell calls either helper today.
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
    /// Returns a COM-callable wrapper (CCW) pointer for the MANAGED
    /// object <paramref name="com"/>, for handing that object to native
    /// code. The CCW answers QueryInterface only for the interfaces the
    /// object's own class is generated to expose. Caller MUST Release
    /// the returned pointer when finished.
    /// </summary>
    /// <remarks>
    /// Never call this on an RCW to reach another interface of the
    /// native object behind it: a CCW over an RCW exposes only IUnknown,
    /// so that QueryInterface fails with E_NOINTERFACE. Cast the RCW
    /// instead (see the class remarks).
    /// Replacement for the runtime-only
    /// <see cref="Marshal.GetIUnknownForObject"/>, which warns
    /// SYSLIB1099 against <c>[GeneratedComInterface]</c> targets.
    /// </remarks>
    public static IntPtr GetComInterfaceForObject(object com)
        => s_wrappers.GetOrCreateComInterfaceForObject(com, CreateComInterfaceFlags.None);
}
