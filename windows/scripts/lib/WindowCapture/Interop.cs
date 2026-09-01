using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace WindowCapture;

/// <summary>
/// The three things Windows.Graphics.Capture cannot do from managed code on
/// its own: make a D3D device, wrap it as the WinRT device the frame pool
/// wants, and turn an HWND into a capture item.
///
/// Everything else in this tool is plain projected WinRT, which is why the
/// project has no NuGet dependency and no hand-declared ID3D11Device
/// vtable. What is below is the minimum that could not be avoided.
/// </summary>
internal static unsafe class Interop
{
    [Flags]
    private enum DeviceFlags : uint
    {
        None = 0,
        BgraSupport = 0x20,
    }

    [DllImport("d3d11", ExactSpelling = true, PreserveSig = false)]
    private static extern void D3D11CreateDevice(
        nint adapter,
        uint driverType,
        nint software,
        DeviceFlags flags,
        nint featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint device,
        out uint featureLevel,
        out nint immediateContext);

    [DllImport("d3d11", ExactSpelling = true, PreserveSig = false)]
    private static extern nint CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice);

    // The HSTRING is built by hand rather than declared as
    // MarshalAs(UnmanagedType.HString): the default marshaller in .NET 10
    // refuses that combination outright (MarshalDirectiveException,
    // "Invalid managed/unmanaged type combination"), and two more flat
    // combase calls are cheaper than working out which interop stack would
    // have accepted it.
    [DllImport("combase", PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        nint activatableClassId, in Guid iid, out nint factory);

    [DllImport("combase", PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        uint length,
        out nint hstring);

    [DllImport("combase", PreserveSig = false)]
    private static extern void WindowsDeleteString(nint hstring);

    private const uint DriverTypeHardware = 1;
    private const uint D3D11SdkVersion = 7;

    private static readonly Guid IID_IDXGIDevice =
        new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private static readonly Guid IID_IGraphicsCaptureItemInterop =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    // IGraphicsCaptureItem, the ABI INTERFACE, written out rather than
    // taken from typeof(GraphicsCaptureItem).GUID: that property answers
    // the projected runtime class's own id, which CreateForWindow does not
    // implement and refuses with E_NOINTERFACE.
    private static readonly Guid IID_IGraphicsCaptureItem =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// A BGRA-capable hardware D3D device, handed back as the WinRT
    /// <see cref="IDirect3DDevice"/> the frame pool takes.
    ///
    /// BGRA support is not optional: the pool is created for
    /// B8G8R8A8UIntNormalized, and a device without the flag fails pool
    /// creation with an E_INVALIDARG that says nothing about why.
    /// </summary>
    internal static IDirect3DDevice CreateDirect3DDevice()
    {
        D3D11CreateDevice(
            adapter: 0,
            driverType: DriverTypeHardware,
            software: 0,
            flags: DeviceFlags.BgraSupport,
            featureLevels: 0,
            featureLevelCount: 0,
            sdkVersion: D3D11SdkVersion,
            device: out var device,
            featureLevel: out _,
            immediateContext: out var context);

        if (context != 0) Marshal.Release(context);

        nint dxgi = 0;
        nint inspectable = 0;
        try
        {
            var iid = IID_IDXGIDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgi));
            inspectable = CreateDirect3D11DeviceFromDXGIDevice(dxgi);
            return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (device != 0) Marshal.Release(device);
        }
    }

    /// <summary>
    /// The HWND door into capture. GraphicsCaptureItem has no public
    /// constructor from a window; the activation factory's interop
    /// interface is the documented route, and the picker UI is the only
    /// alternative, which an unattended harness cannot use.
    ///
    /// Called through the vtable rather than a declared interface on
    /// purpose: how a declared COM interface is marshalled depends on which
    /// COM interop stack is active in the process, and a function pointer
    /// at a known slot does not. The ABI it targets is frozen.
    /// </summary>
    internal static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, (uint)className.Length, out var classId);
        nint factory;
        try
        {
            RoGetActivationFactory(
                classId, in IID_IGraphicsCaptureItemInterop, out factory);
        }
        finally
        {
            WindowsDeleteString(classId);
        }

        nint itemAbi = 0;
        try
        {
            // Slot 3. IGraphicsCaptureItemInterop derives from IUnknown,
            // NOT IInspectable -- which is worth stating because the
            // neighbouring WinRT interop interfaces mostly do, and
            // assuming it here calls straight past the end of the vtable.
            // That reads whatever follows as a function pointer and
            // returns arbitrary HRESULTs (0x8050D340, 0x806411A0 were the
            // two seen) that decode to nothing and name nothing.
            var vtbl = *(void***)factory;
            var createForWindow =
                (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtbl[3];

            var iid = IID_IGraphicsCaptureItem;
            nint result;
            Marshal.ThrowExceptionForHR(
                createForWindow(factory, hwnd, &iid, &result));
            itemAbi = result;
            return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemAbi);
        }
        finally
        {
            if (itemAbi != 0) Marshal.Release(itemAbi);
            if (factory != 0) Marshal.Release(factory);
        }
    }
}
