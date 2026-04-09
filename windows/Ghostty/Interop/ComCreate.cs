using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Ghostty.Interop;

/// <summary>
/// Trim/AOT-safe replacement for the legacy
/// <c>CoCreateInstance(out object)</c> P/Invoke. Returns the raw
/// IUnknown* via <c>out IntPtr</c> and lets a
/// <see cref="StrategyBasedComWrappers"/> instance wrap it into a
/// strongly-typed managed interface.
///
/// Why this exists: <c>[GeneratedComInterface]</c> types are the
/// modern way to declare COM contracts on .NET 8+ and the only
/// path that survives <c>PublishAot</c> (CsWinRT #1927). The legacy
/// <c>[MarshalAs(UnmanagedType.Interface)] out object</c> signature
/// requires runtime marshalling and silently breaks under the
/// trimmer. Migrating CoCreateInstance to <c>out IntPtr</c> + a
/// shared <see cref="ComWrappers"/> instance lets every call site
/// use the generated COM path without each one re-implementing
/// QueryInterface.
/// </summary>
internal static partial class ComCreate
{
    /// <summary>
    /// Shared ComWrappers instance. Strategy-based wrappers handle
    /// QueryInterface dispatch across every <c>[GeneratedComInterface]</c>
    /// the runtime knows about, so a single instance is enough for
    /// the whole process.
    /// </summary>
    private static readonly StrategyBasedComWrappers s_wrappers = new();

    public const uint CLSCTX_INPROC_SERVER = 0x1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    /// <summary>
    /// Activate <paramref name="clsid"/> and query for
    /// <paramref name="iid"/>, then wrap the resulting IUnknown* in
    /// a strongly-typed managed object. Caller is expected to cast
    /// the returned object to the matching <c>[GeneratedComInterface]</c>
    /// type. Throws on HRESULT failure.
    /// </summary>
    public static object Create(Guid clsid, Guid iid)
    {
        var hr = CoCreateInstance(in clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, in iid, out var ppv);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return s_wrappers.GetOrCreateObjectForComInstance(ppv, CreateObjectFlags.None);
        }
        finally
        {
            // GetOrCreateObjectForComInstance AddRefs the unknown for
            // its own lifetime tracking, so we release the reference
            // CoCreateInstance gave us.
            Marshal.Release(ppv);
        }
    }

    /// <summary>
    /// Wrap an existing IUnknown* in a managed object. Used when
    /// QueryInterface'ing across <c>[GeneratedComInterface]</c>
    /// types — e.g. an <c>IShellLinkW</c> instance also exposing
    /// <c>IPropertyStore</c>.
    /// </summary>
    public static object Wrap(IntPtr unknown)
        => s_wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.None);

    /// <summary>
    /// Reverse of <see cref="Wrap"/>: hand back the IUnknown* for a
    /// managed RCW that was created via the shared
    /// <see cref="StrategyBasedComWrappers"/>. Replacement for the
    /// runtime-only <see cref="Marshal.GetIUnknownForObject"/>,
    /// which warns SYSLIB1099 against <c>[GeneratedComInterface]</c>
    /// targets. Caller owns the returned reference and must
    /// <see cref="Marshal.Release"/> it.
    /// </summary>
    public static IntPtr GetIUnknown(object com)
        => s_wrappers.GetOrCreateComInterfaceForObject(com, CreateComInterfaceFlags.None);
}
