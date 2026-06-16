// COM interop for Microsoft.UI.Xaml.Controls.SwapChainPanel. The managed
// SwapChainPanel type does not expose a way to attach a native swap chain,
// so we QueryInterface for the native interop interfaces and call them
// manually.
//
// libghostty's Windows renderer (inside ghostty.dll) presents into a
// DirectComposition surface handle rather than binding a swap chain object
// to the panel. We pass the panel via
// ghostty_surface_config_s.platform.windows.swap_chain_panel purely as the
// mode selector (the renderer only checks it for non-null), then after the
// surface is created we fetch the surface handle with
// ghostty_surface_get_swap_chain_handle and hand it to the panel via
// ISwapChainPanelNative2::SetSwapChainHandle. Binding the handle instead of
// the swap chain object lets DWM composite the panel as soon as the window
// is shown and keeps the binding valid across ResizeBuffers.
//
// hand-written: WinUI 3 SwapChainPanel responds to the legacy XAML interop
// IIDs, which CsWin32-generated bindings don't surface (they emit the
// Microsoft.UI.Xaml IID instead). We call through the raw v-table so the
// path stays NativeAOT-safe (no ComWrappers marshalling required).

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using WinRT;

namespace Ghostty.Interop;

[GeneratedComInterface]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
internal partial interface ISwapChainPanelNative
{
    [PreserveSig]
    int SetSwapChain(IntPtr swapChain); // IDXGISwapChain*
}

internal static class SwapChainPanelInterop
{
    private static readonly Guid IID_ISwapChainPanelNative =
        new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    // ISwapChainPanelNative2 inherits ISwapChainPanelNative; its v-table is
    // IUnknown (0-2), SetSwapChain (3), SetSwapChainHandle (4).
    private static readonly Guid IID_ISwapChainPanelNative2 =
        new("88fd8248-10da-4810-bb4c-010dd27faea9");

    /// <summary>
    /// QueryInterfaces a WinUI 3 SwapChainPanel for ISwapChainPanelNative
    /// and returns the raw interface pointer, used as the SwapChainPanel
    /// mode selector passed to ghostty_surface_new. libghostty does not
    /// call through this pointer (it presents into a composition surface
    /// handle instead), so the caller MUST Release it once SurfaceNew
    /// returns. The managed SwapChainPanel keeps composition alive via its
    /// own ref.
    /// </summary>
    public static IntPtr QueryInterface(Microsoft.UI.Xaml.Controls.SwapChainPanel panel)
    {
        var objRef = ((IWinRTObject)panel).NativeObject;
        var iid = IID_ISwapChainPanelNative;
        var hr = Marshal.QueryInterface(objRef.ThisPtr, in iid, out var ppv);
        if (hr < 0 || ppv == IntPtr.Zero)
            throw new InvalidOperationException(
                $"QueryInterface for ISwapChainPanelNative failed: 0x{hr:X8}");
        return ppv;
    }

    /// <summary>
    /// Bind a DirectComposition surface handle (from
    /// ghostty_surface_get_swap_chain_handle) to the panel via
    /// ISwapChainPanelNative2::SetSwapChainHandle. Call on the UI thread
    /// that owns the panel, after the surface (and its swap chain) exist.
    /// </summary>
    public static void SetSwapChainHandle(
        Microsoft.UI.Xaml.Controls.SwapChainPanel panel,
        IntPtr swapChainHandle)
    {
        var objRef = ((IWinRTObject)panel).NativeObject;
        var iid = IID_ISwapChainPanelNative2;
        var hr = Marshal.QueryInterface(objRef.ThisPtr, in iid, out var ppv);
        if (hr < 0 || ppv == IntPtr.Zero)
            throw new InvalidOperationException(
                $"QueryInterface for ISwapChainPanelNative2 failed: 0x{hr:X8}");

        try
        {
            unsafe
            {
                var vtbl = *(IntPtr*)ppv;
                // slot 4: SetSwapChainHandle(HANDLE swapChainHandle)
                var setSwapChainHandle =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)(*((IntPtr*)vtbl + 4));
                var shr = setSwapChainHandle(ppv, swapChainHandle);
                if (shr < 0)
                    throw new InvalidOperationException(
                        $"ISwapChainPanelNative2::SetSwapChainHandle failed: 0x{shr:X8}");
            }
        }
        finally
        {
            Marshal.Release(ppv);
        }
    }

    /// <summary>
    /// Release a pointer obtained from <see cref="QueryInterface"/>. Safe on
    /// IntPtr.Zero.
    /// </summary>
    public static void Release(IntPtr ppv)
    {
        if (ppv != IntPtr.Zero) Marshal.Release(ppv);
    }
}
