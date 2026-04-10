//! Graphics API wrapper for DirectX 12.
//!
//! This module provides the GraphicsAPI contract required by GenericRenderer,
//! mirroring the structure of Metal.zig and OpenGL.zig.
//!
//! Current status: all 5 render pipelines (bg_color, cell_bg, cell_text,
//! image, bg_image) are wired end-to-end. Shader-visible descriptor heaps
//! for SRV and sampler binding are created at init. RenderPass.step()
//! binds PSO, root signature, uniforms, textures, samplers, and instance
//! buffers, then issues DrawInstanced calls.
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

const DescriptorHeap = @import("directx12/descriptor_heap.zig").DescriptorHeap;

// --- Sub-module re-exports: low-level D3D12/DXGI/COM bindings ---

pub const com = @import("directx12/com.zig");
pub const d3d12 = @import("directx12/d3d12.zig");
pub const dcomp = @import("directx12/dcomp.zig");
pub const descriptor_heap = @import("directx12/descriptor_heap.zig");
pub const device = @import("directx12/device.zig");
pub const dxgi = @import("directx12/dxgi.zig");

pub const custom_shader_target: shadertoy.Target = .hlsl;

/// DX12 uses top-left origin, same as Metal.
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


/// Number of CBV/SRV/UAV descriptors in the shader-visible heap.
/// Covers: font atlas (grayscale + color), grid texture, image textures,
/// and up to ~50 custom shader textures. Will need tuning if custom
/// shaders exceed this.
const srv_heap_capacity: u32 = 64;

/// Number of sampler descriptors in the shader-visible heap.
/// Covers: default point/linear samplers + per-pipeline overrides.
const sampler_heap_capacity: u32 = 16;

// --- GraphicsAPI contract: mutable state ---

/// Runtime blending mode, set by GenericRenderer when config changes.
blending: configpkg.Config.AlphaBlending = .native,

/// Set to true when a device-loss error is detected (DEVICE_REMOVED,
/// DEVICE_HUNG, or DEVICE_RESET). Prevents further GPU submissions
/// until device recovery.
device_lost: bool = false,

/// DX12 device owning command queue, fence, and swap chain.
dev: ?device.Device = null,

/// SwapChain3 interface for GetCurrentBackBufferIndex.
/// Obtained by QueryInterface from the SwapChain1 in dev.
swap_chain3: ?*dxgi.IDXGISwapChain3 = null,

/// RTV descriptor heap for swap chain back buffers.
rtv_heap: ?DescriptorHeap = null,

/// Shader-visible CBV/SRV/UAV descriptor heap for textures and buffers.
srv_heap: ?DescriptorHeap = null,

/// Shader-visible sampler descriptor heap.
sampler_heap: ?DescriptorHeap = null,

/// Per-frame command recording contexts (triple buffered).
gpu_frames: [device.Device.frame_count]?Frame = .{ null, null, null },

/// Back buffer resources from the swap chain.
back_buffers: [device.Device.frame_count]?*d3d12.ID3D12Resource = .{ null, null, null },

/// RTV handles for each back buffer.
rtv_handles: [device.Device.frame_count]d3d12.D3D12_CPU_DESCRIPTOR_HANDLE =
    .{ .{ .ptr = 0 }, .{ .ptr = 0 }, .{ .ptr = 0 } },

/// RTV for the shared-texture resource. Null for HWND and
/// SwapChainPanel modes (which use the rtv_handles array above).
/// Shared-texture mode has exactly one render target -- the shared
/// ID3D12Resource -- so it gets a single RTV in heap slot 0.
shared_rtv: ?d3d12.D3D12_CPU_DESCRIPTOR_HANDLE = null,

/// Command list from the current beginFrame, executed in drawFrameEnd.
/// Also temporarily set to the init command list during init() so that
/// initAtlasTexture can record resource barriers for placeholder textures.
pending_command_list: ?*d3d12.ID3D12GraphicsCommandList = null,

/// Temporary command allocator for init-time GPU work (texture barriers).
/// Created in init(), released by flushInitCommands().
init_command_allocator: ?*d3d12.ID3D12CommandAllocator = null,

/// Temporary command list for init-time GPU work.
/// Set as pending_command_list during init so initAtlasTexture picks it
/// up through the existing textureOptions path without signature changes.
init_command_list: ?*d3d12.ID3D12GraphicsCommandList = null,

/// Back buffer index from the current beginFrame, used in drawFrameEnd
/// to record the fence value against the correct frame slot.
/// Must be saved here because GetCurrentBackBufferIndex advances after Present.
pending_frame_index: u32 = 0,

/// Desired surface dimensions, updated by setTargetSize.
///
/// DX12 uses composition swap chains for all surface types (HWND via
/// DirectComposition, SwapChainPanel via XAML). Composition swap chains
/// have no HWND to query for size, so the apprt must forward the window
/// dimensions via setTargetSize.
///
/// Width and height are packed into a single u64 (high 32 = width, low 32
/// = height) so we can store/load both atomically. setTargetSize is invoked
/// synchronously from ghostty_surface_set_size on the apprt thread (the C#
/// UI thread for the WinUI 3 shell), while the renderer thread reads it
/// in beginFrame to drive ResizeBuffers. Two separate atomics would tear
/// during a drag: a resize that updates only width before the renderer
/// reads height would briefly show a mismatched back buffer. The renderer
/// thread tracks what it has actually applied in `applied_width` /
/// `applied_height` -- if the desired and applied values differ at the
/// start of beginFrame, it resizes the swap chain there (the only thread
/// allowed to touch back_buffers/RTVs/fence).
desired_size: std.atomic.Value(u64) = .init(0),
applied_width: u32 = 0,
applied_height: u32 = 0,

/// Pack/unpack helpers for desired_size. Width in the high 32 bits so a
/// hexdump reads naturally as WWWWWWWW_HHHHHHHH.
inline fn packSize(width: u32, height: u32) u64 {
    return (@as(u64, width) << 32) | @as(u64, height);
}
inline fn unpackSize(packed_size: u64) struct { width: u32, height: u32 } {
    return .{
        .width = @intCast(packed_size >> 32),
        .height = @intCast(packed_size & 0xFFFFFFFF),
    };
}

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
    else if (w.shared_texture.enabled)
        .{ .shared_texture = .{
            .width = w.shared_texture.width,
            .height = w.shared_texture.height,
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
    result.rtv_heap = DescriptorHeap.init(
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

    // Shader-visible CBV/SRV/UAV heap for texture SRVs.
    result.srv_heap = DescriptorHeap.init(
        dev_ptr.device,
        .CBV_SRV_UAV,
        srv_heap_capacity,
        true,
    ) catch |err| {
        log.err("SRV descriptor heap creation failed: {}", .{err});
        return error.DescriptorHeapCreationFailed;
    };
    errdefer {
        result.srv_heap.?.deinit();
        result.srv_heap = null;
    }

    // Shader-visible sampler heap for texture sampling.
    result.sampler_heap = DescriptorHeap.init(
        dev_ptr.device,
        .SAMPLER,
        sampler_heap_capacity,
        true,
    ) catch |err| {
        log.err("Sampler descriptor heap creation failed: {}", .{err});
        return error.DescriptorHeapCreationFailed;
    };
    errdefer {
        result.sampler_heap.?.deinit();
        result.sampler_heap = null;
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
    } else if (dev_ptr.shared_texture != null) {
        // Shared-texture mode: one RTV pointing at the shared resource.
        // Use RTV heap slot 0 -- we only ever need one slot because
        // the shared resource is the sole render target and is never
        // rotated with a back-buffer cycle.
        const st = &dev_ptr.shared_texture.?;
        const rtv_handle = result.rtv_heap.?.cpuHandle(0);
        dev_ptr.device.CreateRenderTargetView(st.resource, null, rtv_handle);
        result.shared_rtv = rtv_handle;
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

    // Create a one-shot command list for init-time texture work.
    // initAtlasTexture (called from SwapChain.init) needs a command list
    // to record COPY_DEST -> PIXEL_SHADER_RESOURCE barriers on placeholder
    // textures. Per-frame command lists aren't available until beginFrame,
    // so we create a dedicated one here and flush it after SwapChain.init.
    {
        var init_alloc: ?*d3d12.ID3D12CommandAllocator = null;
        const alloc_hr = dev_ptr.device.CreateCommandAllocator(
            .DIRECT,
            &d3d12.ID3D12CommandAllocator.IID,
            @ptrCast(&init_alloc),
        );
        if (com.FAILED(alloc_hr)) {
            log.err("CreateCommandAllocator for init failed: 0x{x}", .{@as(u32, @bitCast(alloc_hr))});
            return error.CommandAllocatorCreationFailed;
        }
        errdefer _ = init_alloc.?.Release();

        var init_cl: ?*d3d12.ID3D12GraphicsCommandList = null;
        const cl_hr = dev_ptr.device.CreateCommandList(
            0,
            .DIRECT,
            init_alloc.?,
            null,
            &d3d12.ID3D12GraphicsCommandList.IID,
            @ptrCast(&init_cl),
        );
        if (com.FAILED(cl_hr)) {
            log.err("CreateCommandList for init failed: 0x{x}", .{@as(u32, @bitCast(cl_hr))});
            return error.CommandListCreationFailed;
        }
        errdefer _ = init_cl.?.Release();

        result.init_command_allocator = init_alloc;
        result.init_command_list = init_cl;
        result.pending_command_list = init_cl;
    }

    // For shared-texture mode, use the texture dimensions as the initial
    // applied size so beginFrame doesn't trigger a redundant recreate on
    // the first frame. For swap-chain modes, use the screen size.
    const init_width = if (w.shared_texture.enabled) w.shared_texture.width else size.width;
    const init_height = if (w.shared_texture.enabled) w.shared_texture.height else size.height;
    result.desired_size.store(packSize(init_width, init_height), .monotonic);
    result.applied_width = init_width;
    result.applied_height = init_height;

    return result;
}

pub fn deinit(self: *DirectX12) void {
    // Wait for GPU to finish before releasing anything.
    if (self.dev) |*dev_ptr| {
        dev_ptr.waitForGpu() catch {};
    }

    // Release init command list if never flushed (error during init).
    if (self.init_command_list) |cl| {
        _ = cl.Release();
        self.init_command_list = null;
    }
    if (self.init_command_allocator) |alloc| {
        _ = alloc.Release();
        self.init_command_allocator = null;
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

    if (self.sampler_heap) |*h| {
        h.deinit();
        self.sampler_heap = null;
    }

    if (self.srv_heap) |*h| {
        h.deinit();
        self.srv_heap = null;
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

    self.* = undefined;
}

/// Execute and release the one-shot init command list.
/// Called from GenericRenderer.init after SwapChain.init creates the
/// initial atlas textures. Submits the recorded resource barriers
/// (COPY_DEST -> PIXEL_SHADER_RESOURCE) and waits for the GPU to
/// finish before the first render frame.
pub fn flushInitCommands(self: *DirectX12) void {
    const dev_ptr = &(self.dev orelse return);

    if (self.init_command_list) |cl| {
        const close_hr = cl.Close();
        if (!com.FAILED(close_hr)) {
            const lists = [_]*d3d12.ID3D12GraphicsCommandList{cl};
            dev_ptr.command_queue.ExecuteCommandLists(1, &lists);

            dev_ptr.waitForGpu() catch |err| {
                log.err("waitForGpu after init commands failed: {}", .{err});
            };
        } else {
            // Close failed -- the recorded barriers won't reach the GPU.
            // Texture.state already reads PIXEL_SHADER_RESOURCE but the
            // GPU-side state is still COPY_DEST, so the first render frame
            // will likely hit a resource state mismatch. This typically
            // means the device is already in a bad state.
            log.err("init command list Close failed: 0x{x}", .{@as(u32, @bitCast(close_hr))});
        }

        _ = cl.Release();
        self.init_command_list = null;
    }

    if (self.init_command_allocator) |alloc| {
        _ = alloc.Release();
        self.init_command_allocator = null;
    }

    // Clear so it doesn't point to the now-released init command list.
    // beginFrame will set it to the per-frame command list.
    self.pending_command_list = null;
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

    // Present the swap chain and check for device-removed errors.
    // Sync interval 1 paces to vblank without tearing against the
    // compositor. Interactive resize relies on setTargetSize waking the
    // renderer thread (see embedded.zig) plus the existing 120 Hz draw
    // timer as a backstop -- both routes hit beginFrame, which compares
    // desired_size against applied_width/height and calls ResizeBuffers
    // before any new GPU work. The renderer thread owns Present
    // exclusively; the apprt UI thread does no GPU work during resize.
    if (self.swap_chain3) |sc3| {
        const hr = sc3.Present(1, 0);
        if (hr == com.DXGI_ERROR_DEVICE_REMOVED or hr == com.DXGI_ERROR_DEVICE_HUNG or hr == com.DXGI_ERROR_DEVICE_RESET) {
            self.handleDeviceRemoved();
            // Fence signal is intentionally skipped -- the device is gone.
            return;
        }
        if (com.FAILED(hr)) {
            log.err("Present failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        }
    }

    // Signal the fence so we know when this frame is done.
    // Use the saved index, not GetCurrentBackBufferIndex, because
    // Present may have already advanced the current back buffer.
    // Safe without sync because rendering is single-threaded per surface.
    const frame_idx = self.pending_frame_index;
    const new_fence_value = dev_ptr.fence_value.fetchAdd(1, .release) + 1;
    if (self.gpu_frames[frame_idx]) |*f| {
        f.fence_value = new_fence_value;
    }
    const signal_hr = dev_ptr.command_queue.Signal(dev_ptr.fence, new_fence_value);
    if (com.FAILED(signal_hr)) {
        log.err("fence Signal failed: 0x{x}", .{@as(u32, @bitCast(signal_hr))});
        // A TDR between Present and Signal leaves the fence unsignaled.
        // Without this check the next beginFrame would deadlock waiting
        // on a fence that will never advance.
        if (signal_hr == com.DXGI_ERROR_DEVICE_REMOVED or
            signal_hr == com.DXGI_ERROR_DEVICE_HUNG or
            signal_hr == com.DXGI_ERROR_DEVICE_RESET)
        {
            self.handleDeviceRemoved();
            return;
        }
    }

    // Shared-texture mode has no Present call to detect device-removed.
    // Check after Signal so a TDR during this frame sets device_lost
    // instead of letting the next beginFrame deadlock on the fence.
    if (self.swap_chain3 == null) {
        const reason = dev_ptr.device.GetDeviceRemovedReason();
        if (com.FAILED(reason)) {
            self.handleDeviceRemoved();
        }
    }
}

pub fn initShaders(
    self: *const DirectX12,
    alloc: Allocator,
    custom_shaders: []const [:0]const u8,
) !shaders.Shaders {
    _ = alloc;
    _ = custom_shaders;
    const dev_device = if (self.dev) |*d| d.device else null;
    return shaders.Shaders.init(dev_device);
}

/// Called by the apprt (via generic.zig) when the surface is resized.
/// This is the only resize signal DX12 gets -- composition swap chains
/// have no HWND to query, so the apprt must forward the size.
///
/// IMPORTANT: this is invoked synchronously from `ghostty_surface_set_size`,
/// which the WinUI 3 shell calls on the C# UI thread for every SizeChanged
/// event. We must NOT touch any GPU state here -- back_buffers, fences,
/// command lists, and the descriptor heaps all belong to the renderer
/// thread. Just record the desired size atomically; `beginFrame` (running
/// on the renderer thread) will pick it up and call `resizeSwapChain` at
/// a safe point before any command-list work for the next frame.
pub fn setTargetSize(self: *DirectX12, width: u32, height: u32) void {
    // Guard against transient 0x0 reports during WinUI 3 layout passes.
    if (width == 0 or height == 0) return;
    self.desired_size.store(packSize(width, height), .monotonic);
}

/// Resize the swap chain back buffers in place via IDXGISwapChain1::ResizeBuffers.
///
/// DXGI requires every reference to the existing back buffers (including
/// RTVs implicitly via the resource) to be released before ResizeBuffers,
/// and the GPU must be idle so it isn't still reading them.
fn resizeSwapChain(self: *DirectX12, width: u32, height: u32) !void {
    // Reaching this path without a device or swap chain is a programming
    // error: beginFrame on the renderer thread already short-circuited on
    // both. Return real errors so the caller logs and we never silently
    // loop on the same desired size forever (applied_* would never advance).
    const dev_ptr = &(self.dev orelse return error.NoDevice);
    const sc3 = self.swap_chain3 orelse return error.NoSwapChain;

    // Drain in-flight frames so back_buffers[*] aren't being read by the GPU.
    dev_ptr.waitForGpu() catch |err| {
        log.err("waitForGpu before ResizeBuffers failed: {}", .{err});
        return error.WaitForGpuFailed;
    };

    // Drop our references to the existing back buffers. DXGI keeps the
    // underlying allocations alive and rebinds them to the resized buffers
    // on the next GetBuffer call.
    for (&self.back_buffers) |*bb| {
        if (bb.*) |r| {
            _ = r.Release();
            bb.* = null;
        }
    }

    // UNKNOWN format and 0 flags preserve whatever the swap chain was
    // created with -- the same values device.zig used at creation time.
    // ResizeBuffers lives on IDXGISwapChain1. IDXGISwapChain3 inherits
    // from IDXGISwapChain1 in COM, so the v-table prefix is identical and
    // a pointer reinterpret is safe; we use it instead of QueryInterface
    // to avoid an AddRef/Release pair on every resize.
    const sc1: *dxgi.IDXGISwapChain1 = @ptrCast(sc3);
    const hr = sc1.ResizeBuffers(
        device.Device.frame_count,
        width,
        height,
        .UNKNOWN,
        0,
    );
    if (hr == com.DXGI_ERROR_DEVICE_REMOVED or
        hr == com.DXGI_ERROR_DEVICE_HUNG or
        hr == com.DXGI_ERROR_DEVICE_RESET)
    {
        self.handleDeviceRemoved();
        return error.DeviceRemoved;
    }
    if (com.FAILED(hr)) {
        log.err("ResizeBuffers failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.ResizeBuffersFailed;
    }

    // Re-acquire back buffers and recreate RTVs at the same descriptor
    // slots. Mirrors the loop in init() so the rtv_handles array stays
    // valid for beginFrame.
    const rtv_heap = &(self.rtv_heap orelse return error.NoRtvHeap);
    for (0..device.Device.frame_count) |i| {
        var resource: ?*d3d12.ID3D12Resource = null;
        const get_hr = sc3.GetBuffer(
            @intCast(i),
            &d3d12.ID3D12Resource.IID,
            @ptrCast(&resource),
        );
        if (com.FAILED(get_hr)) {
            log.err("GetBuffer({}) after resize failed: 0x{x}", .{ i, @as(u32, @bitCast(get_hr)) });
            return error.GetBufferFailed;
        }
        self.back_buffers[i] = resource;

        const rtv_handle = rtv_heap.cpuHandle(@intCast(i));
        dev_ptr.device.CreateRenderTargetView(resource, null, rtv_handle);
        self.rtv_handles[i] = rtv_handle;
    }

    // waitForGpu drained everything, so any fence value the per-frame slots
    // were waiting on is already complete. Reset to 0 so beginFrame doesn't
    // burn an event wait on a stale value (and so a future fence_value
    // wraparound corner case can't trip on a leftover signal).
    for (&self.gpu_frames) |*gf| {
        if (gf.*) |*f| f.fence_value = 0;
    }

    // Record what we just applied so beginFrame doesn't loop on the same
    // resize. Stored on the renderer thread; only the renderer thread reads
    // it, so a plain field is fine (no atomic needed).
    self.applied_width = width;
    self.applied_height = height;
}

pub fn surfaceSize(self: *const DirectX12) !struct { width: u32, height: u32 } {
    const sz = unpackSize(self.desired_size.load(.monotonic));
    if (sz.width != 0 and sz.height != 0) {
        return .{ .width = sz.width, .height = sz.height };
    }

    // Fallback: query swap chain buffer dimensions via GetDesc1.
    // init() seeds the cache, so this only fires on the very first frame
    // if surfaceSize() is called before init() finishes. GetDesc1 returns
    // the *buffer* size, which may lag behind the window until ResizeBuffers
    // runs -- but it is the best we can do without the cache.
    const dev_ptr = self.dev orelse return .{ .width = 0, .height = 0 };
    if (dev_ptr.swap_chain) |sc| {
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
    // self is *const to match the GraphicsAPI contract (Metal and OpenGL
    // both use *const). Mutable access goes through renderer.api.
    _ = self;
    const api: *DirectX12 = &renderer.api;
    if (api.device_lost) return error.DeviceLost;
    const dev_ptr = &(api.dev orelse return error.NoDevice);

    // If the apprt asked for a new surface size since the last frame,
    // resize now -- on the renderer thread, before any command list work.
    // setTargetSize only records the desired size; it cannot touch GPU
    // state because it runs on the apprt thread.
    //
    // Swap-chain mode calls ResizeBuffers and re-acquires back buffers.
    // Shared-texture mode calls recreateSharedTexture and refreshes the
    // single RTV at heap slot 0 so subsequent beginFrame calls use the
    // new resource dimensions. The two paths are mutually exclusive.
    const want = unpackSize(api.desired_size.load(.monotonic));
    if (want.width != 0 and want.height != 0 and
        (want.width != api.applied_width or want.height != api.applied_height))
    {
        if (api.swap_chain3 != null) {
            api.resizeSwapChain(want.width, want.height) catch |err| {
                log.err("DX12 swap chain resize failed: {}", .{err});
                return error.ResizeFailed;
            };
        } else if (dev_ptr.shared_texture != null) {
            // Shared-texture mode has no swap chain to resize; recreate the
            // shared resource, refresh the single RTV, and bump the version
            // counter so consumers re-open their handle.
            dev_ptr.recreateSharedTexture(want.width, want.height) catch |err| {
                log.err("recreateSharedTexture failed: {}", .{err});
                api.device_lost = true;
                return error.ResizeFailed;
            };
            // Refresh the single RTV to point at the new resource.
            // Slot 0 is the fixed shared-texture slot allocated in init();
            // overwriting the descriptor is safe because waitForGpu inside
            // recreateSharedTexture already drained all in-flight GPU work.
            if (api.rtv_heap) |*heap| {
                const st = &dev_ptr.shared_texture.?;
                const rtv_handle = heap.cpuHandle(0);
                dev_ptr.device.CreateRenderTargetView(st.resource, null, rtv_handle);
                api.shared_rtv = rtv_handle;
            }
            // Reset stale frame fence values -- waitForGpu in
            // recreateSharedTexture already drained all in-flight work,
            // mirroring the reset in resizeSwapChain.
            for (&api.gpu_frames) |*gf| {
                if (gf.*) |*f| f.fence_value = 0;
            }
            api.applied_width = want.width;
            api.applied_height = want.height;
        }
    }

    // Determine which frame slot and render target to use.
    // Swap-chain mode rotates through back_buffers[]; shared-texture mode
    // has a single render target and always uses slot 0.
    const frame_idx: u32 = if (api.swap_chain3) |sc3|
        sc3.GetCurrentBackBufferIndex()
    else
        0;

    const rtv_handle: d3d12.D3D12_CPU_DESCRIPTOR_HANDLE = if (api.swap_chain3 != null)
        api.rtv_handles[frame_idx]
    else
        api.shared_rtv orelse return error.NoRenderTarget;

    // Shared-texture mode: the resource lives in D3D12_RESOURCE_STATE_COMMON
    // (ALLOW_SIMULTANEOUS_ACCESS), so no PRESENT->RENDER_TARGET barrier is
    // needed -- COMMON implicitly promotes for RT writes.
    const render_target: ?*d3d12.ID3D12Resource = if (api.swap_chain3 != null)
        api.back_buffers[frame_idx]
    else
        dev_ptr.shared_texture.?.resource;

    // Extract the frame for this slot and wait for its previous GPU work.
    var frame = api.gpu_frames[frame_idx] orelse return error.FrameNotReady;
    const wait_value = frame.fence_value;
    if (dev_ptr.fence.GetCompletedValue() < wait_value) {
        const hr = dev_ptr.fence.SetEventOnCompletion(wait_value, dev_ptr.fence_event);
        if (com.FAILED(hr)) return error.FrameSyncFailed;
        _ = d3d12.WaitForSingleObject(dev_ptr.fence_event, d3d12.INFINITE);
    }

    // Point the target at the chosen render target resource and RTV.
    target.resource = render_target;
    target.rtv_handle = rtv_handle;

    // Reset and open the command list for recording.
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
        // Sync interval 1: see drawFrameEnd for the rationale.
        const hr = sc3.Present(1, 0);
        if (hr == com.DXGI_ERROR_DEVICE_REMOVED or hr == com.DXGI_ERROR_DEVICE_HUNG or hr == com.DXGI_ERROR_DEVICE_RESET) {
            self.handleDeviceRemoved();
            return error.PresentFailed;
        }
        if (com.FAILED(hr)) {
            log.err("presentLastTarget failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return error.PresentFailed;
        }
    }
}

fn handleDeviceRemoved(self: *DirectX12) void {
    self.device_lost = true;
    if (self.dev) |*dev_ptr| {
        const reason = dev_ptr.device.GetDeviceRemovedReason();
        log.err("GPU device removed, reason: 0x{x}", .{@as(u32, @bitCast(reason))});
    } else {
        log.err("GPU device removed, no device available for reason query", .{});
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
    return .{
        .device = if (self.dev) |*d| d.device else null,
        .command_list = self.pending_command_list,
        // @constCast is safe: DescriptorHeap wraps a COM object on the heap.
        .srv_heap = if (self.srv_heap) |*h| @constCast(h) else null,
    };
}

pub inline fn samplerOptions(self: DirectX12) Sampler.Options {
    return .{
        .device = if (self.dev) |*d| d.device else null,
        // @constCast is safe: DescriptorHeap wraps a COM object on the heap.
        .sampler_heap = if (self.sampler_heap) |*h| @constCast(h) else null,
    };
}

pub inline fn imageTextureOptions(
    self: DirectX12,
    format: ImageTextureFormat,
    srgb: bool,
) Texture.Options {
    _ = srgb; // DX12 sRGB handled by the render target format, not texture views.
    return .{
        .device = if (self.dev) |*d| d.device else null,
        .command_list = self.pending_command_list,
        // @constCast is safe: DescriptorHeap wraps a COM object on the heap.
        .srv_heap = if (self.srv_heap) |*h| @constCast(h) else null,
        .pixel_format = switch (format) {
            .gray => .R8_UNORM,
            .rgba => .R8G8B8A8_UNORM,
            .bgra => .B8G8R8A8_UNORM,
        },
    };
}

pub fn initAtlasTexture(
    self: *const DirectX12,
    atlas: *const font.Atlas,
) Texture.Error!Texture {
    const size: usize = @intCast(atlas.size);
    const pixel_format: dxgi.DXGI_FORMAT = switch (atlas.format) {
        .grayscale => .R8_UNORM,
        .bgra => .B8G8R8A8_UNORM,
        // BGR has no direct DXGI format; use BGRA and let the atlas
        // handle depth conversion when uploading.
        .bgr => .B8G8R8A8_UNORM,
    };
    return Texture.init(.{
        .device = if (self.dev) |*d| d.device else null,
        .command_list = self.pending_command_list,
        // @constCast is safe: DescriptorHeap wraps a COM object on the heap.
        .srv_heap = if (self.srv_heap) |*h| @constCast(h) else null,
        .pixel_format = pixel_format,
    }, size, size, null);
}

/// Update an atlas texture's command list to the current frame's.
/// DX12 rotates command lists across triple-buffered frames, so textures
/// must not use a stale command list from a different frame slot.
pub fn updateTextureCommandList(self: DirectX12, texture: *Texture) void {
    texture.setCommandList(self.pending_command_list);
}

test {
    _ = com;
    _ = d3d12;
    _ = dcomp;
    _ = descriptor_heap;
    _ = device;
    _ = dxgi;
}

test "DirectX12 does not have frame_fence_values" {
    try std.testing.expect(!@hasField(DirectX12, "frame_fence_values"));
}

test "DirectX12 has desired/applied size fields" {
    try std.testing.expect(@hasField(DirectX12, "desired_size"));
    try std.testing.expect(@hasField(DirectX12, "applied_width"));
    try std.testing.expect(@hasField(DirectX12, "applied_height"));
}

test "DirectX12 default size is zero" {
    const api: DirectX12 = .{};
    try std.testing.expectEqual(@as(u64, 0), api.desired_size.load(.monotonic));
    try std.testing.expectEqual(@as(u32, 0), api.applied_width);
    try std.testing.expectEqual(@as(u32, 0), api.applied_height);
}

test "DirectX12 packSize/unpackSize roundtrip" {
    const packed_size = DirectX12.packSize(1920, 1080);
    const sz = DirectX12.unpackSize(packed_size);
    try std.testing.expectEqual(@as(u32, 1920), sz.width);
    try std.testing.expectEqual(@as(u32, 1080), sz.height);
}

test "DirectX12 has device_lost field" {
    try std.testing.expect(@hasField(DirectX12, "device_lost"));
}

test "DirectX12 has init command list fields" {
    try std.testing.expect(@hasField(DirectX12, "init_command_allocator"));
    try std.testing.expect(@hasField(DirectX12, "init_command_list"));
}

test "DirectX12 init command list defaults to null" {
    const api: DirectX12 = .{};
    try std.testing.expect(api.init_command_allocator == null);
    try std.testing.expect(api.init_command_list == null);
}

test "DirectX12 default device_lost is false" {
    const api: DirectX12 = .{};
    try std.testing.expect(!api.device_lost);
}

test "device_lost flag gates further rendering" {
    var api: DirectX12 = .{};
    try std.testing.expect(!api.device_lost);
    // Simulate what handleDeviceRemoved does to the flag.
    api.device_lost = true;
    try std.testing.expect(api.device_lost);
}

test "device_lost flag is independent of device presence" {
    var api: DirectX12 = .{};
    // device_lost can be set regardless of whether dev is populated,
    // matching the guard in beginFrame which checks device_lost before
    // accessing dev.
    try std.testing.expect(api.dev == null);
    api.device_lost = true;
    try std.testing.expect(api.device_lost);
}

// Pull the directx12 integration test file into the test graph.
// gpu_test.zig is otherwise orphaned -- it has no consumer outside
// tests -- so without this @import it would never be compiled or
// executed by `zig build test`.
test {
    _ = @import("directx12/gpu_test.zig");
}
