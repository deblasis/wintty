const std = @import("std");
const log = std.log.scoped(.directx11);
const com = @import("com.zig");
const dxgi = @import("dxgi.zig");
const d3d11 = @import("d3d11.zig");

const HRESULT = com.HRESULT;
const GUID = com.GUID;
const IUnknown = com.IUnknown;
const IDXGISwapChain = dxgi.IDXGISwapChain;
const HWND = dxgi.HWND;

pub const Surface = union(enum) {
    hwnd: HWND,
    swap_chain_panel: *dxgi.ISwapChainPanelNative,
};

pub const RECT = extern struct {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
};

extern "user32" fn GetClientRect(hWnd: HWND, lpRect: *RECT) callconv(.winapi) i32;

pub const Device = struct {
    device: *d3d11.ID3D11Device,
    context: *d3d11.ID3D11DeviceContext,
    swap_chain: *dxgi.IDXGISwapChain1,
    panel_native: ?*dxgi.ISwapChainPanelNative,
    rtv: ?*d3d11.ID3D11RenderTargetView,
    blend_state: ?*d3d11.ID3D11BlendState,
    /// The HWND for querying the actual window size.
    /// Null for composition (SwapChainPanel) surfaces.
    hwnd: ?HWND,
    width: u32,
    height: u32,
    /// Desired size set by the embedder (via ghostty_surface_set_size).
    /// Used by windowSize() for composition surfaces where there is no
    /// HWND to query. Zero means "use current buffer size".
    target_width: u32 = 0,
    target_height: u32 = 0,

    pub const InitError = error{
        DeviceCreationFailed,
        QueryInterfaceFailed,
        GetAdapterFailed,
        GetFactoryFailed,
        SwapChainCreationFailed,
        SetSwapChainFailed,
        BackBufferFailed,
        RenderTargetViewFailed,
        BlendStateCreationFailed,
    };

    pub fn init(surface: Surface, w_in: u32, h_in: u32) InitError!Device {
        // Clamp to at least 1x1 -- CreateSwapChainForComposition with 0x0
        // produces a degenerate swap chain that ResizeBuffers cannot recover.
        const width = @max(w_in, 1);
        const height = @max(h_in, 1);
        log.info("init called: size={}x{}", .{ width, height });

        // Create D3D11 device and immediate context.
        var device: ?*d3d11.ID3D11Device = null;
        var context: ?*d3d11.ID3D11DeviceContext = null;
        const feature_levels = [_]d3d11.D3D_FEATURE_LEVEL{.@"11_0"};
        var hr = d3d11.D3D11CreateDevice(
            null, // default adapter
            .HARDWARE,
            null, // no software rasterizer
            d3d11.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            &feature_levels,
            feature_levels.len,
            d3d11.D3D11_SDK_VERSION,
            &device,
            null, // don't need actual feature level back
            &context,
        );
        if (com.FAILED(hr)) {
            log.err("D3D11CreateDevice failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return InitError.DeviceCreationFailed;
        }

        const dev = device.?;
        errdefer _ = dev.Release();
        const ctx = context.?;
        errdefer _ = ctx.Release();

        log.debug("D3D11CreateDevice OK: device=0x{x}", .{@intFromPtr(dev)});

        // QueryInterface device -> IDXGIDevice
        var dxgi_device_opt: ?*anyopaque = null;
        hr = dev.QueryInterface(&dxgi.IDXGIDevice.IID, &dxgi_device_opt);
        if (com.FAILED(hr) or dxgi_device_opt == null) {
            log.err("QI for IDXGIDevice failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return InitError.QueryInterfaceFailed;
        }
        const dxgi_device: *dxgi.IDXGIDevice = @ptrCast(@alignCast(dxgi_device_opt.?));
        defer _ = dxgi_device.Release();

        // Get the adapter from the DXGI device.
        var adapter: ?*dxgi.IDXGIAdapter = null;
        hr = dxgi_device.GetAdapter(&adapter);
        if (com.FAILED(hr) or adapter == null) {
            log.err("GetAdapter failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return InitError.GetAdapterFailed;
        }
        defer _ = adapter.?.Release();

        // Get IDXGIFactory2 from the adapter.
        var factory_opt: ?*anyopaque = null;
        hr = adapter.?.GetParent(&dxgi.IDXGIFactory2.IID, &factory_opt);
        if (com.FAILED(hr) or factory_opt == null) {
            log.err("GetParent(IDXGIFactory2) failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return InitError.GetFactoryFailed;
        }
        const factory: *dxgi.IDXGIFactory2 = @ptrCast(@alignCast(factory_opt.?));
        defer _ = factory.Release();

        // Create swap chain. Descriptor differs by surface type:
        // - HWND: opaque window, DXGI_ALPHA_MODE_UNSPECIFIED
        // - Composition: premultiplied alpha for XAML integration
        var swap_chain: ?*dxgi.IDXGISwapChain1 = null;
        var panel_native: ?*dxgi.ISwapChainPanelNative = null;

        var desc = dxgi.DXGI_SWAP_CHAIN_DESC1{
            .Width = width,
            .Height = height,
            .Format = .B8G8R8A8_UNORM,
            .Stereo = 0,
            .SampleDesc = .{ .Count = 1, .Quality = 0 },
            .BufferUsage = dxgi.DXGI_USAGE_RENDER_TARGET_OUTPUT,
            .BufferCount = 2,
            .Scaling = .NONE,
            .SwapEffect = .FLIP_DISCARD,
            .AlphaMode = .UNSPECIFIED,
            .Flags = 0,
        };

        switch (surface) {
            .hwnd => |hwnd| {
                hr = factory.CreateSwapChainForHwnd(
                    @ptrCast(dev),
                    hwnd,
                    &desc,
                    null,
                    null,
                    &swap_chain,
                );
            },
            .swap_chain_panel => |panel| {
                panel_native = panel;
                desc.AlphaMode = .PREMULTIPLIED;
                // Composition surfaces need scaling -- the panel may not
                // match the buffer size exactly, unlike HWND surfaces.
                desc.Scaling = .STRETCH;
                hr = factory.CreateSwapChainForComposition(
                    @ptrCast(dev),
                    &desc,
                    null,
                    &swap_chain,
                );
            },
        }
        if (com.FAILED(hr) or swap_chain == null) {
            log.err("swap chain creation failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return InitError.SwapChainCreationFailed;
        }
        const sc = swap_chain.?;
        errdefer _ = sc.Release();

        // For composition surfaces, attach swap chain to the panel.
        if (panel_native) |panel| {
            hr = panel.SetSwapChain(@ptrCast(sc));
            if (com.FAILED(hr)) {
                log.err("SetSwapChain failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
                return InitError.SetSwapChainFailed;
            }
        }
        errdefer if (panel_native) |panel| {
            _ = panel.SetSwapChain(null);
        };

        // Get the back buffer and create a render target view.
        const rtv = createRenderTargetView(dev, sc) orelse {
            log.err("createRenderTargetView failed", .{});
            return InitError.RenderTargetViewFailed;
        };
        errdefer _ = rtv.Release();

        // Create a premultiplied-alpha blend state so that translucent
        // pixels (e.g. padding cells output as float4(0,0,0,0) by the
        // cell_bg shader) blend over the background instead of
        // overwriting it with transparent black.
        const blend_state = createBlendState(dev) orelse {
            log.err("createBlendState failed", .{});
            return InitError.BlendStateCreationFailed;
        };

        log.info("device initialised: {}x{}", .{ width, height });

        return Device{
            .device = dev,
            .context = ctx,
            .swap_chain = sc,
            .panel_native = panel_native,
            .rtv = rtv,
            .blend_state = blend_state,
            .hwnd = switch (surface) {
                .hwnd => |h| h,
                .swap_chain_panel => null,
            },
            .width = width,
            .height = height,
        };
    }

    pub fn deinit(self: *Device) void {
        // Release render target view.
        if (self.rtv) |rtv| {
            _ = rtv.Release();
            self.rtv = null;
        }

        // Release blend state.
        if (self.blend_state) |bs| {
            _ = bs.Release();
            self.blend_state = null;
        }

        // Detach swap chain from the panel (composition surfaces only).
        if (self.panel_native) |panel| {
            _ = panel.SetSwapChain(null);
        }

        // Release in reverse creation order.
        _ = self.swap_chain.Release();
        _ = self.context.Release();
        _ = self.device.Release();
    }

    /// Return the desired surface size.
    /// HWND path: queries GetClientRect for the actual window size.
    /// Composition path: returns the target size set by the embedder
    /// via setTargetSize, falling back to the current buffer size.
    pub fn windowSize(self: *const Device) struct { width: u32, height: u32 } {
        if (self.hwnd) |hwnd| {
            var rc: RECT = undefined;
            if (GetClientRect(hwnd, &rc) != 0) {
                const w: u32 = @intCast(rc.right - rc.left);
                const h: u32 = @intCast(rc.bottom - rc.top);
                if (w > 0 and h > 0) return .{ .width = w, .height = h };
            }
        }
        // Composition: use embedder-supplied target size if available.
        if (self.target_width > 0 and self.target_height > 0) {
            return .{ .width = self.target_width, .height = self.target_height };
        }
        return .{ .width = self.width, .height = self.height };
    }

    /// Set the desired surface size for composition surfaces.
    /// Called when the embedder reports a size change (ghostty_surface_set_size).
    /// The actual swap chain resize happens in beginFrame when the renderer
    /// detects the mismatch between windowSize() and the buffer dimensions.
    pub fn setTargetSize(self: *Device, width: u32, height: u32) void {
        self.target_width = width;
        self.target_height = height;
    }

    pub const ResizeError = error{
        ResizeBuffersFailed,
        RenderTargetViewFailed,
    };

    pub fn resize(self: *Device, width: u32, height: u32) ResizeError!void {
        // Release current render target view.
        if (self.rtv) |rtv| {
            _ = rtv.Release();
            self.rtv = null;
        }

        // Resize swap chain buffers.
        const hr = self.swap_chain.ResizeBuffers(0, width, height, .UNKNOWN, 0);
        if (com.FAILED(hr)) {
            log.err("IDXGISwapChain1::ResizeBuffers failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return ResizeError.ResizeBuffersFailed;
        }

        // Recreate render target view.
        self.rtv = createRenderTargetView(self.device, self.swap_chain) orelse {
            return ResizeError.RenderTargetViewFailed;
        };

        self.width = width;
        self.height = height;
    }

    pub const PresentError = error{
        PresentFailed,
    };

    pub fn present(self: *Device) PresentError!void {
        const hr = self.swap_chain.Present(1, 0);
        if (com.FAILED(hr)) {
            log.err("IDXGISwapChain1::Present failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return PresentError.PresentFailed;
        }
    }

    /// Create a premultiplied-alpha blend state.
    /// SRC_BLEND=ONE, DEST_BLEND=INV_SRC_ALPHA implements standard
    /// premultiplied alpha compositing: out = src + dst * (1 - src.a).
    fn createBlendState(device: *d3d11.ID3D11Device) ?*d3d11.ID3D11BlendState {
        var blend_desc = d3d11.D3D11_BLEND_DESC{};
        blend_desc.RenderTarget[0] = .{
            .BlendEnable = 1,
            .SrcBlend = .ONE,
            .DestBlend = .INV_SRC_ALPHA,
            .BlendOp = .ADD,
            .SrcBlendAlpha = .ONE,
            .DestBlendAlpha = .INV_SRC_ALPHA,
            .BlendOpAlpha = .ADD,
            .RenderTargetWriteMask = 0x0F,
        };

        var state: ?*d3d11.ID3D11BlendState = null;
        const hr = device.CreateBlendState(&blend_desc, &state);
        if (com.FAILED(hr) or state == null) {
            log.err("ID3D11Device::CreateBlendState failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return null;
        }
        return state.?;
    }

    /// Get the back buffer from the swap chain and create a render target view.
    fn createRenderTargetView(
        device: *d3d11.ID3D11Device,
        swap_chain: *dxgi.IDXGISwapChain1,
    ) ?*d3d11.ID3D11RenderTargetView {
        // Get back buffer as ID3D11Texture2D.
        var back_buffer_opt: ?*anyopaque = null;
        var hr = swap_chain.GetBuffer(0, &d3d11.ID3D11Texture2D.IID, &back_buffer_opt);
        if (com.FAILED(hr) or back_buffer_opt == null) {
            log.err("IDXGISwapChain1::GetBuffer failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return null;
        }
        const back_buffer: *d3d11.ID3D11Texture2D = @ptrCast(@alignCast(back_buffer_opt.?));
        defer _ = back_buffer.Release();

        // Create render target view from the back buffer.
        var rtv: ?*d3d11.ID3D11RenderTargetView = null;
        hr = device.CreateRenderTargetView(@ptrCast(back_buffer), null, &rtv);
        if (com.FAILED(hr) or rtv == null) {
            log.err("ID3D11Device::CreateRenderTargetView failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
            return null;
        }

        return rtv.?;
    }
};
