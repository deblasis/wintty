//! Graphics API wrapper for DirectX 12.
//!
//! This module provides the GraphicsAPI contract required by GenericRenderer,
//! mirroring the structure of Metal.zig, OpenGL.zig, and the previous
//! DirectX11.zig.
//!
//! Current status: device init, swap chain, command queue, fence sync,
//! clear render target, and present are functional. The window clears
//! to the background color via the real DX12 pipeline.
pub const DirectX12 = @This();

const builtin = @import("builtin");
const std = @import("std");
const Allocator = std.mem.Allocator;

const configpkg = @import("../config.zig");
const font = @import("../font/main.zig");
const rendererpkg = @import("../renderer.zig");
const Renderer = rendererpkg.GenericRenderer(DirectX12);
const shadertoy = @import("shadertoy.zig");
const log = std.log.scoped(.directx12);

// --- GraphicsAPI contract: types ---

pub const GraphicsAPI = DirectX12;
pub const Target = @import("directx12/Target.zig");
pub const Frame = @import("directx12/Frame.zig");
pub const RenderPass = @import("directx12/RenderPass.zig");
pub const Pipeline = @import("directx12/Pipeline.zig");
pub const Sampler = @import("directx12/Sampler.zig");
pub const Texture = @import("directx12/Texture.zig");

const bufferpkg = @import("directx12/buffer.zig");
pub const Buffer = bufferpkg.Buffer;

pub const shaders = @import("directx12/shaders.zig");

// --- Sub-module re-exports: low-level D3D12/DXGI/COM bindings ---

pub const com = @import("directx12/com.zig");
pub const d3d12 = @import("directx12/d3d12.zig");
pub const dcomp = @import("directx12/dcomp.zig");
pub const descriptor_heap = @import("directx12/descriptor_heap.zig");
pub const device = @import("directx12/device.zig");
pub const dxgi = @import("directx12/dxgi.zig");

// TODO: custom shaders not yet supported on DX12. Using .glsl as placeholder;
// DX12 will need its own shadertoy.Target variant (.hlsl) when custom shaders
// are implemented. Can't add it without modifying upstream shadertoy.zig.
pub const custom_shader_target: shadertoy.Target = .glsl;

/// DX12 uses top-left origin, same as Metal and DX11.
pub const custom_shader_y_is_down = true;

/// Triple buffering for DX12, matching Metal's swap chain depth.
pub const swap_chain_count = 3;

/// Pixel format for image texture options.
pub const ImageTextureFormat = enum {
    /// 1 byte per pixel grayscale.
    gray,
    /// 4 bytes per pixel RGBA.
    rgba,
    /// 4 bytes per pixel BGRA.
    bgra,
};

// --- GraphicsAPI contract: mutable state ---

/// Runtime blending mode, set by GenericRenderer when config changes.
blending: configpkg.Config.AlphaBlending = .native,

/// DX12 device owning command queue, fence, and swap chain.
dev: ?device.Device = null,

/// SwapChain3 interface for GetCurrentBackBufferIndex.
/// Obtained by QueryInterface from the SwapChain1 in dev.
swap_chain3: ?*dxgi.IDXGISwapChain3 = null,

/// RTV descriptor heap for swap chain back buffers.
rtv_heap: ?descriptor_heap.DescriptorHeap = null,

/// Per-frame command recording contexts (triple buffered).
gpu_frames: [device.Device.frame_count]?Frame = .{ null, null, null },

/// Back buffer resources from the swap chain.
back_buffers: [device.Device.frame_count]?*d3d12.ID3D12Resource = .{ null, null, null },

/// RTV handles for each back buffer.
rtv_handles: [device.Device.frame_count]d3d12.D3D12_CPU_DESCRIPTOR_HANDLE =
    .{ .{ .ptr = 0 }, .{ .ptr = 0 }, .{ .ptr = 0 } },

/// Fence values per frame for CPU/GPU sync.
frame_fence_values: [device.Device.frame_count]u64 = .{ 0, 0, 0 },

/// Command list from the current beginFrame, executed in drawFrameEnd.
pending_command_list: ?*d3d12.ID3D12GraphicsCommandList = null,

/// Back buffer index from the current beginFrame, used in drawFrameEnd
/// to record the fence value against the correct frame slot.
/// Must be saved here because GetCurrentBackBufferIndex advances after Present.
pending_frame_index: u32 = 0,

// --- GraphicsAPI contract: functions ---

pub fn init(alloc: Allocator, opts: rendererpkg.Options) !DirectX12 {
    _ = alloc; // DX12 uses COM-based allocation; Zig allocator unused for now.

    var result = DirectX12{};

    if (comptime builtin.os.tag != .windows) {
        return result;
    }

    const surface_pkg = @import("directx12/surface.zig");
    const w = opts.rt_surface.platform.windows;

    const surface: surface_pkg.Surface = if (w.hwnd) |hwnd|
        .{ .hwnd = hwnd }
    else if (w.swap_chain_panel) |panel|
        // panel is an opaque COM-compatible pointer from apprt; alignment is guaranteed.
        .{ .swap_chain_panel = @ptrCast(@alignCast(panel)) }
    else if (w.shared_texture_out) |out_ptr|
        // out_ptr is an opaque pointer from apprt; alignment is guaranteed.
        .{ .shared_texture = .{
            .handle_out = @ptrCast(@alignCast(out_ptr)),
            .width = w.texture_width,
            .height = w.texture_height,
        } }
    else
        return error.NoWindowsSurface;

    const size = opts.size.screen;
    result.dev = device.Device.init(surface, .{
        .width = size.width,
        .height = size.height,
    }) catch |err| {
        log.err("DX12 device init failed: {}", .{err});
        return error.DeviceInitFailed;
    };
    errdefer {
        result.dev.?.deinit();
        result.dev = null;
    }

    const dev_ptr = &result.dev.?;

    // Get SwapChain3 for GetCurrentBackBufferIndex.
    if (dev_ptr.swap_chain) |sc| {
        var sc3: ?*dxgi.IDXGISwapChain3 = null;
        const hr = sc.vtable.QueryInterface(
            @ptrCast(sc),
            &dxgi.IDXGISwapChain3.IID,
            @ptrCast(&sc3),
        );
        if (com.FAILED(hr)) {
            log.err("QueryInterface for IDXGISwapChain3 failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return error.SwapChain3QueryFailed;
        }
        result.swap_chain3 = sc3;
    }
    errdefer if (result.swap_chain3) |sc3| {
        _ = sc3.Release();
    };

    // Create RTV descriptor heap for back buffers.
    result.rtv_heap = descriptor_heap.DescriptorHeap.init(
        dev_ptr.device,
        .RTV,
        device.Device.frame_count,
        false,
    ) catch |err| {
        log.err("RTV descriptor heap creation failed: {}", .{err});
        return error.DescriptorHeapCreationFailed;
    };
    errdefer {
        result.rtv_heap.?.deinit();
        result.rtv_heap = null;
    }

    // Get back buffer resources and create RTVs.
    if (result.swap_chain3) |sc3| {
        for (0..device.Device.frame_count) |i| {
            var resource: ?*d3d12.ID3D12Resource = null;
            const hr = sc3.GetBuffer(
                @intCast(i),
                &d3d12.ID3D12Resource.IID,
                @ptrCast(&resource),
            );
            if (com.FAILED(hr)) {
                log.err("GetBuffer({}) failed: 0x{x}", .{ i, @as(u32, @bitCast(hr)) });
                return error.GetBufferFailed;
            }
            result.back_buffers[i] = resource;

            const rtv_handle = result.rtv_heap.?.cpuHandle(@intCast(i));
            dev_ptr.device.CreateRenderTargetView(resource, null, rtv_handle);
            result.rtv_handles[i] = rtv_handle;
        }
    }
    errdefer {
        for (&result.back_buffers) |*bb| {
            if (bb.*) |r| {
                _ = r.Release();
                bb.* = null;
            }
        }
    }

    // Create per-frame command allocators and command lists.
    for (&result.gpu_frames) |*gf| {
        gf.* = Frame.init(dev_ptr.device) catch |err| {
            log.err("Frame init failed: {}", .{err});
            return error.FrameInitFailed;
        };
    }
    errdefer {
        for (&result.gpu_frames) |*gf| {
            if (gf.*) |*f| f.deinit();
        }
    }

    return result;
}

pub fn deinit(self: *DirectX12) void {
    // Wait for GPU to finish before releasing anything.
    if (self.dev) |*dev_ptr| {
        dev_ptr.waitForGpu() catch {};
    }

    for (&self.gpu_frames) |*gf| {
        if (gf.*) |*f| {
            f.deinit();
            gf.* = null;
        }
    }

    for (&self.back_buffers) |*bb| {
        if (bb.*) |r| {
            _ = r.Release();
            bb.* = null;
        }
    }

    if (self.rtv_heap) |*h| {
        h.deinit();
        self.rtv_heap = null;
    }

    if (self.swap_chain3) |sc3| {
        _ = sc3.Release();
        self.swap_chain3 = null;
    }

    if (self.dev) |*dev_ptr| {
        dev_ptr.deinit();
        self.dev = null;
    }
}

pub fn drawFrameStart(self: *DirectX12) void {
    _ = self;
}

pub fn drawFrameEnd(self: *DirectX12) void {
    const dev_ptr = &(self.dev orelse return);
    const cl = self.pending_command_list orelse return;
    self.pending_command_list = null;

    // Execute the command list.
    const lists = [_]*d3d12.ID3D12GraphicsCommandList{cl};
    dev_ptr.command_queue.ExecuteCommandLists(1, &lists);

    // Present the swap chain.
    // TODO: check for DXGI_ERROR_DEVICE_REMOVED and trigger TDR recovery
    // once device-lost handling is implemented.
    if (self.swap_chain3) |sc3| {
        const hr = sc3.Present(1, 0);
        if (com.FAILED(hr)) {
            log.err("Present failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        }
    }

    // Signal the fence so we know when this frame is done.
    // Use the saved index, not GetCurrentBackBufferIndex, because
    // Present may have already advanced the current back buffer.
    // Safe without sync because rendering is single-threaded per surface.
    const frame_idx = self.pending_frame_index;
    dev_ptr.fence_value += 1;
    self.frame_fence_values[frame_idx] = dev_ptr.fence_value;
    const hr = dev_ptr.command_queue.Signal(dev_ptr.fence, dev_ptr.fence_value);
    if (com.FAILED(hr)) {
        log.err("fence Signal failed: 0x{x}", .{@as(u32, @bitCast(hr))});
    }
}

pub fn initShaders(
    self: *const DirectX12,
    alloc: Allocator,
    custom_shaders: []const [:0]const u8,
) !shaders.Shaders {
    _ = self;
    _ = alloc;
    _ = custom_shaders;
    return .{};
}

pub fn setTargetSize(self: *DirectX12, width: u32, height: u32) void {
    _ = self;
    _ = width;
    _ = height;
    // TODO: for composition surfaces, store the target size so
    // surfaceSize() can return it (no HWND to query).
}

pub fn surfaceSize(self: *const DirectX12) !struct { width: u32, height: u32 } {
    const dev_ptr = self.dev orelse return .{ .width = 0, .height = 0 };

    if (dev_ptr.swap_chain) |sc| {
        // Query the swap chain's current buffer dimensions.
        // TODO: GetDesc1 is called every frame -- cache the dimensions and
        // only re-query on resize. For HWND surfaces, store the HWND on
        // Device and query the client rect directly, so resize is detected
        // before the swap chain is resized.
        var desc: dxgi.DXGI_SWAP_CHAIN_DESC1 = undefined;
        const hr = sc.GetDesc1(&desc);
        if (com.SUCCEEDED(hr)) {
            return .{ .width = desc.Width, .height = desc.Height };
        }
        log.warn("GetDesc1 failed: 0x{x}", .{@as(u32, @bitCast(hr))});
    }

    // No swap chain (SharedTexture surface) or query failed.
    return .{ .width = 0, .height = 0 };
}

pub fn initTarget(self: *const DirectX12, width: usize, height: usize) !Target {
    _ = self;
    // Target resource and RTV handle are set in beginFrame when we know
    // which back buffer is current. Start with the dimensions only.
    return .{ .width = width, .height = height };
}

pub inline fn beginFrame(
    self: *const DirectX12,
    renderer: *Renderer,
    target: *Target,
) !Frame {
    // self is *const to match the GraphicsAPI contract (Metal, OpenGL, DX11
    // all use *const). Mutable access goes through renderer.api, same pattern
    // as DX11.
    _ = self;
    const api: *DirectX12 = &renderer.api;
    // SharedTexture surfaces have no swap chain; they need a separate
    // submission path that is not yet implemented.
    const sc3 = api.swap_chain3 orelse return error.NoSwapChain;
    const dev_ptr = &(api.dev orelse return error.NoDevice);

    // Which back buffer does the swap chain want us to render to?
    const frame_idx = sc3.GetCurrentBackBufferIndex();

    // Wait for this frame slot's previous GPU work to finish.
    const wait_value = api.frame_fence_values[frame_idx];
    if (dev_ptr.fence.GetCompletedValue() < wait_value) {
        const hr = dev_ptr.fence.SetEventOnCompletion(wait_value, dev_ptr.fence_event);
        if (com.FAILED(hr)) return error.FrameSyncFailed;
        _ = d3d12.WaitForSingleObject(dev_ptr.fence_event, d3d12.INFINITE);
    }

    // Point the target at this back buffer.
    target.resource = api.back_buffers[frame_idx];
    target.rtv_handle = api.rtv_handles[frame_idx];

    // Reset and open the command list for recording.
    var frame = api.gpu_frames[frame_idx] orelse return error.FrameNotReady;
    try frame.reset();
    frame.renderer = renderer;
    frame.target = target;

    // Write back so the stored copy stays current (the local is a value copy
    // from the optional, not a reference).
    api.gpu_frames[frame_idx] = frame;

    // Save state for drawFrameEnd to execute and signal.
    api.pending_command_list = frame.command_list;
    api.pending_frame_index = frame_idx;

    return frame;
}

pub fn presentLastTarget(self: *DirectX12) !void {
    // Called when no redraw is needed -- re-present the current frame.
    // No new GPU work is submitted, so the existing fence values remain
    // valid and the next beginFrame will wait correctly.
    if (self.swap_chain3) |sc3| {
        const hr = sc3.Present(1, 0);
        if (com.FAILED(hr)) {
            log.err("presentLastTarget failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return error.PresentFailed;
        }
    }
}

pub inline fn bufferOptions(self: DirectX12) bufferpkg.Options {
    return .{
        .device = if (self.dev) |*d| d.device else null,
    };
}

pub const instanceBufferOptions = bufferOptions;
pub const fgBufferOptions = bufferOptions;
pub const imageBufferOptions = bufferOptions;
pub const bgImageBufferOptions = bufferOptions;

pub inline fn bgBufferOptions(self: DirectX12) bufferpkg.Options {
    return self.bufferOptions();
}

pub inline fn uniformBufferOptions(self: DirectX12) bufferpkg.Options {
    return self.bufferOptions();
}

pub inline fn textureOptions(self: DirectX12) Texture.Options {
    _ = self;
    return .{};
}

pub inline fn samplerOptions(self: DirectX12) Sampler.Options {
    _ = self;
    return .{};
}

pub inline fn imageTextureOptions(
    self: DirectX12,
    format: ImageTextureFormat,
    srgb: bool,
) Texture.Options {
    _ = self;
    _ = format;
    _ = srgb;
    return .{};
}

pub fn initAtlasTexture(
    self: *const DirectX12,
    atlas: *const font.Atlas,
) Texture.Error!Texture {
    _ = self;
    return .{ .width = @intCast(atlas.size) };
}

test {
    _ = com;
    _ = d3d12;
    _ = dcomp;
    _ = descriptor_heap;
    _ = device;
    _ = dxgi;
}
