//! Standalone DX11 clear-to-color test harness.
//!
//! This file intentionally duplicates COM types from src/renderer/directx11/
//! to remain a self-contained smoke test with no project imports.
//!
//! Creates a Win32 window, initializes D3D11 via raw COM vtables,
//! and clears to an early-sunrise orange for 3 seconds.
//!
//! Build:
//!   zig build-exe test/windows/test_dx11_clear.zig ^
//!     -lc -ld3d11 -ldxgi ^
//!     -target x86_64-windows-msvc ^
//!     --name test_dx11_clear
//!
//! Run (from repo root):
//!   test_dx11_clear.exe
//!
//! Success = an 800x600 orange window appears for ~3 seconds, then
//! "DX11 clear-to-color test passed." prints and the process exits.

const std = @import("std");
const windows = std.os.windows;

// ---------- basic COM types --------------------------------------------------

const HRESULT = i32;
const GUID = extern struct {
    data1: u32,
    data2: u16,
    data3: u16,
    data4: [8]u8,
};
const IUnknown = extern struct {
    vtable: *const VTable,
    pub const VTable = extern struct {
        QueryInterface: *const fn (*IUnknown, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IUnknown) callconv(.winapi) u32,
        Release: *const fn (*IUnknown) callconv(.winapi) u32,
    };
    pub inline fn QueryInterface(self: *IUnknown, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.QueryInterface(self, riid, pp);
    }
    pub inline fn Release(self: *IUnknown) u32 {
        return self.vtable.Release(self);
    }
};

inline fn FAILED(hr: HRESULT) bool {
    return hr < 0;
}

const Reserved = *const fn () callconv(.winapi) void;

// ---------- DXGI types -------------------------------------------------------

const DXGI_FORMAT = enum(u32) {
    UNKNOWN = 0,
    B8G8R8A8_UNORM = 87,
    _,
};
const DXGI_SWAP_EFFECT = enum(u32) { FLIP_SEQUENTIAL = 3, FLIP_DISCARD = 4, _ };
const DXGI_SCALING = enum(u32) { STRETCH = 0, _ };
const DXGI_ALPHA_MODE = enum(u32) { UNSPECIFIED = 0, _ };
const DXGI_USAGE = u32;
const DXGI_USAGE_RENDER_TARGET_OUTPUT: DXGI_USAGE = 0x00000020;

const DXGI_SAMPLE_DESC = extern struct { Count: u32, Quality: u32 };
const DXGI_SWAP_CHAIN_DESC1 = extern struct {
    Width: u32,
    Height: u32,
    Format: DXGI_FORMAT,
    Stereo: i32,
    SampleDesc: DXGI_SAMPLE_DESC,
    BufferUsage: DXGI_USAGE,
    BufferCount: u32,
    Scaling: DXGI_SCALING,
    SwapEffect: DXGI_SWAP_EFFECT,
    AlphaMode: DXGI_ALPHA_MODE,
    Flags: u32,
};

// IDXGIDevice - we only call GetAdapter (slot 7).
const IDXGIDevice = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x54ec77fa,
        .data2 = 0x1377,
        .data3 = 0x44e6,
        .data4 = .{ 0x8c, 0x32, 0x88, 0xfd, 0x5f, 0x44, 0xc8, 0x4c },
    };
    pub const VTable = extern struct {
        // IUnknown (0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: Reserved,
        // IDXGIObject (3-6)
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        GetPrivateData: Reserved,
        GetParent: Reserved,
        // IDXGIDevice (7)
        GetAdapter: *const fn (*IDXGIDevice, *?*IDXGIAdapter) callconv(.winapi) HRESULT,
    };
    pub inline fn GetAdapter(self: *IDXGIDevice, adapter: *?*IDXGIAdapter) HRESULT {
        return self.vtable.GetAdapter(self, adapter);
    }
    pub inline fn Release(self: *IDXGIDevice) u32 {
        const unk: *IUnknown = @ptrCast(self);
        return unk.vtable.Release(unk);
    }
};

// IDXGIAdapter - we only call GetParent (slot 6, IDXGIObject).
const IDXGIAdapter = extern struct {
    vtable: *const VTable,
    pub const VTable = extern struct {
        // IUnknown (0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: Reserved,
        // IDXGIObject (3-6)
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        GetPrivateData: Reserved,
        GetParent: *const fn (*IDXGIAdapter, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
    };
    pub inline fn GetParent(self: *IDXGIAdapter, riid: *const GUID, parent: *?*anyopaque) HRESULT {
        return self.vtable.GetParent(self, riid, parent);
    }
    pub inline fn Release(self: *IDXGIAdapter) u32 {
        const unk: *IUnknown = @ptrCast(self);
        return unk.vtable.Release(unk);
    }
};

// IDXGISwapChain1 - we call Present (8), GetBuffer (9).
const IDXGISwapChain1 = extern struct {
    vtable: *const VTable,
    pub const VTable = extern struct {
        // IUnknown (0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: Reserved,
        // IDXGIObject (3-6)
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        GetPrivateData: Reserved,
        GetParent: Reserved,
        // IDXGIDeviceSubObject (7)
        GetDevice: Reserved,
        // IDXGISwapChain (8-17)
        Present: *const fn (*IDXGISwapChain1, u32, u32) callconv(.winapi) HRESULT,
        GetBuffer: *const fn (*IDXGISwapChain1, u32, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        SetFullscreenState: Reserved,
        GetFullscreenState: Reserved,
        GetDesc: Reserved,
        ResizeBuffers: Reserved,
        ResizeTarget: Reserved,
        GetContainingOutput: Reserved,
        GetFrameStatistics: Reserved,
        GetLastPresentCount: Reserved,
        // IDXGISwapChain1 (18-28) - not called, reserved
        GetDesc1: Reserved,
        GetFullscreenDesc: Reserved,
        GetHwnd: Reserved,
        GetCoreWindow: Reserved,
        Present1: Reserved,
        IsTemporaryMonoSupported: Reserved,
        GetRestrictToOutput: Reserved,
        SetBackgroundColor: Reserved,
        GetBackgroundColor: Reserved,
        SetRotation: Reserved,
        GetRotation: Reserved,
    };
    pub inline fn Present(self: *IDXGISwapChain1, sync_interval: u32, flags: u32) HRESULT {
        return self.vtable.Present(self, sync_interval, flags);
    }
    pub inline fn GetBuffer(self: *IDXGISwapChain1, buffer: u32, riid: *const GUID, surface: *?*anyopaque) HRESULT {
        return self.vtable.GetBuffer(self, buffer, riid, surface);
    }
    pub inline fn Release(self: *IDXGISwapChain1) u32 {
        const unk: *IUnknown = @ptrCast(self);
        return unk.vtable.Release(unk);
    }
};

// IDXGIFactory2 - we call CreateSwapChainForHwnd (slot 15).
const IDXGIFactory2 = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x50c83a1c,
        .data2 = 0xe072,
        .data3 = 0x4c48,
        .data4 = .{ 0x87, 0xb0, 0x36, 0x30, 0xfa, 0x36, 0xa6, 0xd0 },
    };
    pub const VTable = extern struct {
        // IUnknown (0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: Reserved,
        // IDXGIObject (3-6)
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        GetPrivateData: Reserved,
        GetParent: Reserved,
        // IDXGIFactory (7-11)
        EnumAdapters: Reserved,
        MakeWindowAssociation: Reserved,
        GetWindowAssociation: Reserved,
        CreateSwapChain: Reserved,
        CreateSoftwareAdapter: Reserved,
        // IDXGIFactory1 (12-13)
        EnumAdapters1: Reserved,
        IsCurrent: Reserved,
        // IDXGIFactory2 (14-24)
        IsWindowedStereoEnabled: Reserved,
        CreateSwapChainForHwnd: *const fn (
            *IDXGIFactory2,
            *IUnknown,
            windows.HANDLE,
            *const DXGI_SWAP_CHAIN_DESC1,
            ?*const anyopaque,
            ?*anyopaque,
            *?*IDXGISwapChain1,
        ) callconv(.winapi) HRESULT,
        CreateSwapChainForCoreWindow: Reserved,
        GetSharedResourceAdapterLuid: Reserved,
        RegisterStereoStatusWindow: Reserved,
        RegisterStereoStatusEvent: Reserved,
        UnregisterStereoStatus: Reserved,
        RegisterOcclusionStatusWindow: Reserved,
        RegisterOcclusionStatusEvent: Reserved,
        UnregisterOcclusionStatus: Reserved,
        CreateSwapChainForComposition: Reserved,
    };
    pub inline fn CreateSwapChainForHwnd(
        self: *IDXGIFactory2,
        device: *IUnknown,
        hwnd: windows.HANDLE,
        desc: *const DXGI_SWAP_CHAIN_DESC1,
        fullscreen_desc: ?*const anyopaque,
        restrict_to_output: ?*anyopaque,
        swap_chain: *?*IDXGISwapChain1,
    ) HRESULT {
        return self.vtable.CreateSwapChainForHwnd(
            self,
            device,
            hwnd,
            desc,
            fullscreen_desc,
            restrict_to_output,
            swap_chain,
        );
    }
    pub inline fn Release(self: *IDXGIFactory2) u32 {
        const unk: *IUnknown = @ptrCast(self);
        return unk.vtable.Release(unk);
    }
};

// ---------- D3D11 types ------------------------------------------------------

const D3D_FEATURE_LEVEL = enum(u32) { @"11_0" = 0xb000, _ };
const D3D_DRIVER_TYPE = enum(u32) { HARDWARE = 1, _ };
const D3D11_CREATE_DEVICE_FLAG = u32;
const D3D11_CREATE_DEVICE_BGRA_SUPPORT: D3D11_CREATE_DEVICE_FLAG = 0x20;
const D3D11_SDK_VERSION: u32 = 7;

const D3D11_VIEWPORT = extern struct {
    TopLeftX: f32,
    TopLeftY: f32,
    Width: f32,
    Height: f32,
    MinDepth: f32,
    MaxDepth: f32,
};

// ID3D11Resource - base type for GetBuffer result cast.
const ID3D11Resource = extern struct { vtable: *const anyopaque };

// ID3D11Texture2D - IID needed for GetBuffer.
const ID3D11Texture2D = extern struct {
    vtable: *const anyopaque,
    pub const IID = GUID{
        .data1 = 0x6f15aaf2,
        .data2 = 0xd208,
        .data3 = 0x4e89,
        .data4 = .{ 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c },
    };
    pub inline fn Release(self: *ID3D11Texture2D) u32 {
        const unk: *IUnknown = @ptrCast(self);
        return unk.vtable.Release(unk);
    }
};

// ID3D11RenderTargetView - we call Release.
const ID3D11RenderTargetView = extern struct {
    vtable: *const anyopaque,
    pub const IID = GUID{
        .data1 = 0xdfdba067,
        .data2 = 0x0b8d,
        .data3 = 0x4865,
        .data4 = .{ 0x87, 0x5b, 0xd7, 0xb4, 0x51, 0x6c, 0xc1, 0x64 },
    };
    pub inline fn Release(self: *ID3D11RenderTargetView) u32 {
        const unk: *IUnknown = @ptrCast(self);
        return unk.vtable.Release(unk);
    }
};

// ID3D11DeviceContext - we call OMSetRenderTargets (33), RSSetViewports (44),
// ClearRenderTargetView (50), Release (2).
const ID3D11DeviceContext = extern struct {
    vtable: *const VTable,
    pub const VTable = extern struct {
        // IUnknown (0-2)
        QueryInterface: Reserved,
        AddRef: Reserved,
        Release: *const fn (*ID3D11DeviceContext) callconv(.winapi) u32,
        // ID3D11DeviceChild (3-6)
        GetDevice: Reserved,
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        // ID3D11DeviceContext (7-32)
        VSSetConstantBuffers: Reserved,
        PSSetShaderResources: Reserved,
        PSSetShader: Reserved,
        PSSetSamplers: Reserved,
        VSSetShader: Reserved,
        DrawIndexed: Reserved,
        Draw: Reserved,
        Map: Reserved,
        Unmap: Reserved,
        PSSetConstantBuffers: Reserved,
        IASetInputLayout: Reserved,
        IASetVertexBuffers: Reserved,
        IASetIndexBuffer: Reserved,
        DrawIndexedInstanced: Reserved,
        DrawInstanced: Reserved,
        GSSetConstantBuffers: Reserved,
        GSSetShader: Reserved,
        IASetPrimitiveTopology: Reserved,
        VSSetShaderResources: Reserved,
        VSSetSamplers: Reserved,
        Begin: Reserved,
        End: Reserved,
        GetData: Reserved,
        SetPredication: Reserved,
        GSSetShaderResources: Reserved,
        GSSetSamplers: Reserved,
        // slot 33: OMSetRenderTargets
        OMSetRenderTargets: *const fn (
            *ID3D11DeviceContext,
            u32,
            [*]const ?*ID3D11RenderTargetView,
            ?*anyopaque,
        ) callconv(.winapi) void,
        // slot 34: OMSetRenderTargetsAndUnorderedAccessViews
        OMSetRenderTargetsAndUnorderedAccessViews: Reserved,
        OMSetBlendState: Reserved,
        OMSetDepthStencilState: Reserved,
        SOSetTargets: Reserved,
        DrawAuto: Reserved,
        DrawIndexedInstancedIndirect: Reserved,
        DrawInstancedIndirect: Reserved,
        Dispatch: Reserved,
        DispatchIndirect: Reserved,
        RSSetState: Reserved,
        // slot 44: RSSetViewports
        RSSetViewports: *const fn (*ID3D11DeviceContext, u32, [*]const D3D11_VIEWPORT) callconv(.winapi) void,
        RSSetScissorRects: Reserved,
        CopySubresourceRegion: Reserved,
        CopyResource: Reserved,
        UpdateSubresource: Reserved,
        CopyStructureCount: Reserved,
        // slot 50: ClearRenderTargetView
        ClearRenderTargetView: *const fn (
            *ID3D11DeviceContext,
            *ID3D11RenderTargetView,
            *const [4]f32,
        ) callconv(.winapi) void,
    };
    pub inline fn OMSetRenderTargets(
        self: *ID3D11DeviceContext,
        rtvs: []const ?*ID3D11RenderTargetView,
        dsv: ?*anyopaque,
    ) void {
        self.vtable.OMSetRenderTargets(self, @intCast(rtvs.len), rtvs.ptr, dsv);
    }
    pub inline fn RSSetViewports(self: *ID3D11DeviceContext, viewports: []const D3D11_VIEWPORT) void {
        self.vtable.RSSetViewports(self, @intCast(viewports.len), viewports.ptr);
    }
    pub inline fn ClearRenderTargetView(self: *ID3D11DeviceContext, rtv: *ID3D11RenderTargetView, color: *const [4]f32) void {
        self.vtable.ClearRenderTargetView(self, rtv, color);
    }
    pub inline fn Release(self: *ID3D11DeviceContext) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D11Device - we call CreateRenderTargetView (9), QueryInterface (0),
// Release (2).
const ID3D11Device = extern struct {
    vtable: *const VTable,
    pub const VTable = extern struct {
        // IUnknown (0-2)
        QueryInterface: *const fn (*ID3D11Device, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D11Device) callconv(.winapi) u32,
        Release: *const fn (*ID3D11Device) callconv(.winapi) u32,
        // slots 3-8
        CreateBuffer: Reserved,
        CreateTexture1D: Reserved,
        CreateTexture2D: Reserved,
        CreateTexture3D: Reserved,
        CreateShaderResourceView: Reserved,
        CreateUnorderedAccessView: Reserved,
        // slot 9: CreateRenderTargetView
        CreateRenderTargetView: *const fn (
            *ID3D11Device,
            *ID3D11Resource,
            ?*const anyopaque,
            *?*ID3D11RenderTargetView,
        ) callconv(.winapi) HRESULT,
    };
    pub inline fn QueryInterface(self: *ID3D11Device, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.QueryInterface(self, riid, pp);
    }
    pub inline fn CreateRenderTargetView(
        self: *ID3D11Device,
        resource: *ID3D11Resource,
        desc: ?*const anyopaque,
        rtv: *?*ID3D11RenderTargetView,
    ) HRESULT {
        return self.vtable.CreateRenderTargetView(self, resource, desc, rtv);
    }
    pub inline fn Release(self: *ID3D11Device) u32 {
        return self.vtable.Release(self);
    }
};

// D3D11CreateDevice entry point from d3d11.dll.
const D3D11CreateDevice = @extern(*const fn (
    pAdapter: ?*anyopaque,
    DriverType: D3D_DRIVER_TYPE,
    Software: ?windows.HMODULE,
    Flags: D3D11_CREATE_DEVICE_FLAG,
    pFeatureLevels: ?[*]const D3D_FEATURE_LEVEL,
    FeatureLevels: u32,
    SDKVersion: u32,
    ppDevice: ?*?*ID3D11Device,
    pFeatureLevel: ?*D3D_FEATURE_LEVEL,
    ppImmediateContext: ?*?*ID3D11DeviceContext,
) callconv(.winapi) HRESULT, .{
    .library_name = "d3d11",
    .name = "D3D11CreateDevice",
});

// ---------- Win32 window types -----------------------------------------------

const ATOM = u16;
const WPARAM = usize;
const LPARAM = isize;
const LRESULT = isize;
const WNDPROC = *const fn (windows.HWND, u32, WPARAM, LPARAM) callconv(.winapi) LRESULT;

const WNDCLASSEXW = extern struct {
    cbSize: u32,
    style: u32,
    lpfnWndProc: WNDPROC,
    cbClsExtra: i32,
    cbWndExtra: i32,
    hInstance: windows.HMODULE,
    hIcon: ?windows.HANDLE,
    hCursor: ?windows.HANDLE,
    hbrBackground: ?windows.HANDLE,
    lpszMenuName: ?[*:0]const u16,
    lpszClassName: [*:0]const u16,
    hIconSm: ?windows.HANDLE,
};

const MSG = extern struct {
    hwnd: ?windows.HWND,
    message: u32,
    wParam: WPARAM,
    lParam: LPARAM,
    time: u32,
    pt_x: i32,
    pt_y: i32,
};

const WM_DESTROY: u32 = 0x0002;
const PM_REMOVE: u32 = 0x0001;
const WS_OVERLAPPEDWINDOW: u32 = 0x00CF0000;
const CW_USEDEFAULT: i32 = @bitCast(@as(u32, 0x80000000));

extern "user32" fn RegisterClassExW(lpWndClass: *const WNDCLASSEXW) callconv(.winapi) ATOM;
extern "user32" fn CreateWindowExW(
    dwExStyle: u32,
    lpClassName: [*:0]const u16,
    lpWindowName: [*:0]const u16,
    dwStyle: u32,
    X: i32,
    Y: i32,
    nWidth: i32,
    nHeight: i32,
    hWndParent: ?windows.HWND,
    hMenu: ?windows.HANDLE,
    hInstance: windows.HMODULE,
    lpParam: ?*anyopaque,
) callconv(.winapi) ?windows.HWND;
extern "user32" fn ShowWindow(hWnd: windows.HWND, nCmdShow: i32) callconv(.winapi) i32;
extern "user32" fn PeekMessageW(lpMsg: *MSG, hWnd: ?windows.HWND, wMsgFilterMin: u32, wMsgFilterMax: u32, wRemoveMsg: u32) callconv(.winapi) i32;
extern "user32" fn TranslateMessage(lpMsg: *const MSG) callconv(.winapi) i32;
extern "user32" fn DispatchMessageW(lpMsg: *const MSG) callconv(.winapi) LRESULT;
extern "user32" fn DefWindowProcW(hWnd: windows.HWND, Msg: u32, wParam: WPARAM, lParam: LPARAM) callconv(.winapi) LRESULT;
extern "user32" fn PostQuitMessage(nExitCode: i32) callconv(.winapi) void;
extern "kernel32" fn GetModuleHandleW(lpModuleName: ?[*:0]const u16) callconv(.winapi) ?windows.HMODULE;
extern "kernel32" fn QueryPerformanceCounter(lpPerformanceCount: *i64) callconv(.winapi) i32;
extern "kernel32" fn QueryPerformanceFrequency(lpFrequency: *i64) callconv(.winapi) i32;

fn windowProc(hwnd: windows.HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) callconv(.winapi) LRESULT {
    if (msg == WM_DESTROY) {
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wparam, lparam);
}

// ---------- main -------------------------------------------------------------

pub fn main() !void {
    const class_name = std.unicode.utf8ToUtf16LeStringLiteral("DX11TestClass");
    const window_title = std.unicode.utf8ToUtf16LeStringLiteral("DX11 Clear Test");

    const hinstance = GetModuleHandleW(null) orelse {
        std.debug.print("GetModuleHandleW failed\n", .{});
        return error.GetModuleHandleFailed;
    };

    // Register window class.
    const wc = WNDCLASSEXW{
        .cbSize = @sizeOf(WNDCLASSEXW),
        .style = 0,
        .lpfnWndProc = windowProc,
        .cbClsExtra = 0,
        .cbWndExtra = 0,
        .hInstance = hinstance,
        .hIcon = null,
        .hCursor = null,
        .hbrBackground = null,
        .lpszMenuName = null,
        .lpszClassName = class_name,
        .hIconSm = null,
    };
    if (RegisterClassExW(&wc) == 0) {
        std.debug.print("RegisterClassExW failed\n", .{});
        return error.RegisterClassFailed;
    }

    // Create window.
    const hwnd = CreateWindowExW(
        0,
        class_name,
        window_title,
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        800,
        600,
        null,
        null,
        hinstance,
        null,
    ) orelse {
        std.debug.print("CreateWindowExW failed\n", .{});
        return error.CreateWindowFailed;
    };
    _ = ShowWindow(hwnd, 1); // SW_SHOWNORMAL

    // Create D3D11 device and immediate context.
    var device_opt: ?*ID3D11Device = null;
    var context_opt: ?*ID3D11DeviceContext = null;
    const feature_levels = [_]D3D_FEATURE_LEVEL{.@"11_0"};
    var hr = D3D11CreateDevice(
        null,
        .HARDWARE,
        null,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        &feature_levels,
        feature_levels.len,
        D3D11_SDK_VERSION,
        &device_opt,
        null,
        &context_opt,
    );
    if (FAILED(hr)) {
        std.debug.print("D3D11CreateDevice failed: hr=0x{x}\n", .{@as(u32, @bitCast(hr))});
        return error.D3D11CreateDeviceFailed;
    }
    const device = device_opt.?;
    defer _ = device.Release();
    const context = context_opt.?;
    defer _ = context.Release();

    std.debug.print("D3D11CreateDevice OK\n", .{});

    // QueryInterface device -> IDXGIDevice.
    var dxgi_device_opt: ?*anyopaque = null;
    hr = device.QueryInterface(&IDXGIDevice.IID, &dxgi_device_opt);
    if (FAILED(hr) or dxgi_device_opt == null) {
        std.debug.print("QI IDXGIDevice failed: hr=0x{x}\n", .{@as(u32, @bitCast(hr))});
        return error.QIIDXGIDeviceFailed;
    }
    const dxgi_device: *IDXGIDevice = @ptrCast(@alignCast(dxgi_device_opt.?));
    defer _ = dxgi_device.Release();

    // Get adapter.
    var adapter_opt: ?*IDXGIAdapter = null;
    hr = dxgi_device.GetAdapter(&adapter_opt);
    if (FAILED(hr) or adapter_opt == null) {
        std.debug.print("GetAdapter failed: hr=0x{x}\n", .{@as(u32, @bitCast(hr))});
        return error.GetAdapterFailed;
    }
    const adapter = adapter_opt.?;
    defer _ = adapter.Release();

    // Get IDXGIFactory2 from adapter.
    var factory_opt: ?*anyopaque = null;
    hr = adapter.GetParent(&IDXGIFactory2.IID, &factory_opt);
    if (FAILED(hr) or factory_opt == null) {
        std.debug.print("GetParent IDXGIFactory2 failed: hr=0x{x}\n", .{@as(u32, @bitCast(hr))});
        return error.GetFactoryFailed;
    }
    const factory: *IDXGIFactory2 = @ptrCast(@alignCast(factory_opt.?));
    defer _ = factory.Release();

    // Create swap chain for HWND.
    const sc_desc = DXGI_SWAP_CHAIN_DESC1{
        .Width = 800,
        .Height = 600,
        .Format = .B8G8R8A8_UNORM,
        .Stereo = 0,
        .SampleDesc = .{ .Count = 1, .Quality = 0 },
        .BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
        .BufferCount = 2,
        .Scaling = .STRETCH,
        .SwapEffect = .FLIP_DISCARD,
        .AlphaMode = .UNSPECIFIED,
        .Flags = 0,
    };
    var swap_chain_opt: ?*IDXGISwapChain1 = null;
    hr = factory.CreateSwapChainForHwnd(
        @ptrCast(device),
        hwnd,
        &sc_desc,
        null,
        null,
        &swap_chain_opt,
    );
    if (FAILED(hr) or swap_chain_opt == null) {
        std.debug.print("CreateSwapChainForHwnd failed: hr=0x{x}\n", .{@as(u32, @bitCast(hr))});
        return error.CreateSwapChainFailed;
    }
    const swap_chain = swap_chain_opt.?;
    defer _ = swap_chain.Release();

    std.debug.print("Swap chain created OK\n", .{});

    // Get back buffer and create render target view.
    var back_buffer_opt: ?*anyopaque = null;
    hr = swap_chain.GetBuffer(0, &ID3D11Texture2D.IID, &back_buffer_opt);
    if (FAILED(hr) or back_buffer_opt == null) {
        std.debug.print("GetBuffer failed: hr=0x{x}\n", .{@as(u32, @bitCast(hr))});
        return error.GetBufferFailed;
    }
    const back_buffer: *ID3D11Texture2D = @ptrCast(@alignCast(back_buffer_opt.?));
    defer _ = back_buffer.Release();

    var rtv_opt: ?*ID3D11RenderTargetView = null;
    hr = device.CreateRenderTargetView(@ptrCast(back_buffer), null, &rtv_opt);
    if (FAILED(hr) or rtv_opt == null) {
        std.debug.print("CreateRenderTargetView failed: hr=0x{x}\n", .{@as(u32, @bitCast(hr))});
        return error.CreateRTVFailed;
    }
    const rtv = rtv_opt.?;
    defer _ = rtv.Release();

    std.debug.print("RTV created OK -- running render loop for 3 seconds\n", .{});

    // Early-sunrise orange: R=0.98, G=0.45, B=0.25, A=1.0
    const clear_color = [4]f32{ 0.98, 0.45, 0.25, 1.0 };

    // Measure 3 seconds via QueryPerformanceCounter.
    var freq: i64 = 0;
    var start: i64 = 0;
    _ = QueryPerformanceFrequency(&freq);
    _ = QueryPerformanceCounter(&start);
    const duration: i64 = freq * 3;

    var msg: MSG = std.mem.zeroes(MSG);
    while (true) {
        // Drain the message queue.
        while (PeekMessageW(&msg, null, 0, 0, PM_REMOVE) != 0) {
            _ = TranslateMessage(&msg);
            _ = DispatchMessageW(&msg);
        }

        // Render frame.
        const viewport = D3D11_VIEWPORT{
            .TopLeftX = 0.0,
            .TopLeftY = 0.0,
            .Width = 800.0,
            .Height = 600.0,
            .MinDepth = 0.0,
            .MaxDepth = 1.0,
        };
        context.RSSetViewports(&.{viewport});
        context.OMSetRenderTargets(&.{rtv}, null);
        context.ClearRenderTargetView(rtv, &clear_color);
        hr = swap_chain.Present(1, 0);
        if (FAILED(hr)) {
            std.debug.print("Present failed: hr=0x{x}\n", .{@as(u32, @bitCast(hr))});
            return error.PresentFailed;
        }

        // Check elapsed time.
        var now: i64 = 0;
        _ = QueryPerformanceCounter(&now);
        if (now - start >= duration) break;
    }

    std.debug.print("DX11 clear-to-color test passed.\n", .{});
}
