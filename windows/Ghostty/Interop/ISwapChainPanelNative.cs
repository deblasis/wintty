// COM interop for Microsoft.UI.Xaml.Controls.SwapChainPanel. The managed
// SwapChainPanel type does not expose a way to attach a native swap chain
// directly, so we QueryInterface for ISwapChainPanelNative and call
// SetSwapChain(IDXGISwapChain*) manually.
//
// libghostty's Windows renderer lives inside ghostty.dll and uses the panel
// handle we pass via ghostty_surface_config_s.platform.windows.swap_chain_panel.
// We hand it the IUnknown* of the panel (which is QI-able to
// ISwapChainPanelNative), so nothing on the C# side actually calls
// SetSwapChain - but we keep the interface here for any case where the
// shell needs to present its own swap chain (overlays, inspector, etc).
//
// hand-written: WinUI 3 SwapChainPanel responds to the legacy XAML interop
// IID 63aad0b8-7c24-40ff-85a8-640d944cc325, which CsWin32-generated bindings
// don't surface (they emit the Microsoft.UI.Xaml IID instead).

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

    /// <summary>
    /// QueryInterfaces a WinUI 3 SwapChainPanel for ISwapChainPanelNative
    /// and returns the raw interface pointer. libghostty's DX12 renderer
    /// calls SetSwapChain (v-table slot 3) directly on the pointer we
    /// pass, so it must already be the ISwapChainPanelNative v-table, not
    /// the IUnknown of the WinRT projection.
    /// </summary>
    /// <remarks>
    /// Returns an AddRef'd pointer. libghostty's DX12 device init only uses
    /// the pointer synchronously during ghostty_surface_new (it calls
    /// ISwapChainPanelNative::SetSwapChain once and never stores it), so the
    /// caller MUST Release this pointer immediately after SurfaceNew returns.
    /// Confirmed in src/renderer/directx12/device.zig (swap_chain_panel
    /// branch only calls SetSwapChain, no field retention). The managed
    /// SwapChainPanel keeps composition alive via its own ref.
    /// </remarks>
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
    /// Release a pointer obtained from <see cref="QueryInterface"/>. Safe on
    /// IntPtr.Zero.
    /// </summary>
    public static void Release(IntPtr ppv)
    {
        if (ppv != IntPtr.Zero) Marshal.Release(ppv);
    }
}
