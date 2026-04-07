//! DX12 device, command queue, and fence.
//!
//! Owns the core GPU objects needed before anything can be rendered:
//! ID3D12Device, a DIRECT command queue, a fence for CPU/GPU sync,
//! and the DXGI swap chain (with DirectComposition for HWND surfaces).
//!
//! Supports all three surface modes:
//! - HWND: standalone windows, uses DirectComposition
//! - SwapChainPanel: WinUI 3 / XAML hosts
//! - SharedTexture: offscreen / game engine embedding (no swap chain)
pub const Device = @This();

const std = @import("std");
const builtin = @import("builtin");

const com = @import("com.zig");
const d3d12 = @import("d3d12.zig");
const dcomp = @import("dcomp.zig");
const dxgi = @import("dxgi.zig");

const GUID = com.GUID;
const HRESULT = com.HRESULT;
const SUCCEEDED = com.SUCCEEDED;
const FAILED = com.FAILED;

const log = std.log.scoped(.directx12);

/// Number of back buffers (triple buffering).
pub const frame_count: u32 = 3;

// --- Device state ---

device: *d3d12.ID3D12Device,
command_queue: *d3d12.ID3D12CommandQueue,
fence: *d3d12.ID3D12Fence,
fence_value: u64,
fence_event: std.os.windows.HANDLE,

swap_chain: ?*dxgi.IDXGISwapChain1,

// DirectComposition objects, only used for HWND surfaces.
dcomp_device: ?*dcomp.IDCompositionDevice,
dcomp_target: ?*dcomp.IDCompositionTarget,
dcomp_visual: ?*dcomp.IDCompositionVisual,

pub const InitOptions = struct {
    /// Initial back buffer width. Ignored for SharedTexture (uses its own size).
    width: u32 = 800,
    /// Initial back buffer height. Ignored for SharedTexture (uses its own size).
    height: u32 = 600,
};

pub fn init(surface: @import("surface.zig").Surface, opts: InitOptions) !Device {
    // -- Debug layer (debug builds only) --
    if (comptime builtin.mode == .Debug) {
        enableDebugLayer();
    }

    // -- DXGI factory --
    const factory_flags: u32 = if (comptime builtin.mode == .Debug)
        dxgi.DXGI_CREATE_FACTORY_DEBUG
    else
        0;

    var factory: ?*dxgi.IDXGIFactory2 = null;
    {
        const hr = dxgi.CreateDXGIFactory2(
            factory_flags,
            &dxgi.IDXGIFactory2.IID,
            @ptrCast(&factory),
        );
        if (FAILED(hr)) {
            log.err("CreateDXGIFactory2 failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return error.DXGIFactoryCreationFailed;
        }
    }
    defer _ = factory.?.Release();

    // -- Device --
    // Pass null adapter to let DXGI pick the default GPU.
    var device: ?*d3d12.ID3D12Device = null;
    {
        const hr = d3d12.D3D12CreateDevice(
            null,
            d3d12.D3D_FEATURE_LEVEL_12_0,
            &d3d12.ID3D12Device.IID,
            @ptrCast(&device),
        );
        if (FAILED(hr)) {
            log.err("D3D12CreateDevice failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return error.DeviceCreationFailed;
        }
    }
    errdefer _ = device.?.Release();

    const dev = device.?;

    // -- Command queue --
    var command_queue: ?*d3d12.ID3D12CommandQueue = null;
    {
        const desc = d3d12.D3D12_COMMAND_QUEUE_DESC{
            .Type = .DIRECT,
            .Priority = 0,
            .Flags = .NONE,
            .NodeMask = 0,
        };
        const hr = dev.CreateCommandQueue(
            &desc,
            &d3d12.ID3D12CommandQueue.IID,
            @ptrCast(&command_queue),
        );
        if (FAILED(hr)) {
            log.err("CreateCommandQueue failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return error.CommandQueueCreationFailed;
        }
    }
    errdefer _ = command_queue.?.Release();

    // -- Fence --
    var fence: ?*d3d12.ID3D12Fence = null;
    {
        const hr = dev.CreateFence(
            0,
            .NONE,
            &d3d12.ID3D12Fence.IID,
            @ptrCast(&fence),
        );
        if (FAILED(hr)) {
            log.err("CreateFence failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return error.FenceCreationFailed;
        }
    }
    errdefer _ = fence.?.Release();

    const fence_event = d3d12.CreateEventW(null, 0, 0, null) orelse {
        log.err("CreateEventW failed for fence event", .{});
        return error.FenceEventCreationFailed;
    };
    errdefer _ = d3d12.CloseHandle(fence_event);

    // -- Swap chain + composition (surface-dependent) --
    var swap_chain: ?*dxgi.IDXGISwapChain1 = null;
    var dcomp_device_ptr: ?*dcomp.IDCompositionDevice = null;
    var dcomp_target_ptr: ?*dcomp.IDCompositionTarget = null;
    var dcomp_visual_ptr: ?*dcomp.IDCompositionVisual = null;

    switch (surface) {
        .hwnd => |hwnd| {
            // HWND surface: composition swap chain + DirectComposition.
            // DX12 command queues implement IUnknown, which DXGI needs.
            swap_chain = try createCompositionSwapChain(
                factory.?,
                command_queue.?,
                opts.width,
                opts.height,
            );
            errdefer _ = swap_chain.?.Release();

            // Wire up DirectComposition: device -> target -> visual -> swap chain.
            dcomp_device_ptr = try createDCompDevice();
            errdefer _ = dcomp_device_ptr.?.Release();

            dcomp_target_ptr = try createDCompTarget(dcomp_device_ptr.?, hwnd);
            errdefer _ = dcomp_target_ptr.?.Release();

            dcomp_visual_ptr = try createDCompVisual(dcomp_device_ptr.?, swap_chain.?);
            errdefer _ = dcomp_visual_ptr.?.Release();

            // Set the visual as root of the composition target.
            var hr = dcomp_target_ptr.?.SetRoot(dcomp_visual_ptr.?);
            if (FAILED(hr)) {
                log.err("IDCompositionTarget.SetRoot failed: 0x{x}", .{@as(u32, @bitCast(hr))});
                return error.DCompSetRootFailed;
            }

            hr = dcomp_device_ptr.?.Commit();
            if (FAILED(hr)) {
                log.err("IDCompositionDevice.Commit failed: 0x{x}", .{@as(u32, @bitCast(hr))});
                return error.DCompCommitFailed;
            }
        },
        .swap_chain_panel => |panel| {
            // SwapChainPanel surface: composition swap chain, panel owns composition.
            swap_chain = try createCompositionSwapChain(
                factory.?,
                command_queue.?,
                opts.width,
                opts.height,
            );
            errdefer _ = swap_chain.?.Release();

            // Tell the panel about the swap chain.
            const hr = panel.SetSwapChain(@ptrCast(swap_chain.?));
            if (FAILED(hr)) {
                log.err("ISwapChainPanelNative.SetSwapChain failed: 0x{x}", .{@as(u32, @bitCast(hr))});
                return error.SwapChainPanelBindFailed;
            }
        },
        .shared_texture => {
            // SharedTexture: no swap chain, rendering goes to a shared texture.
            // The shared texture resource will be created by the caller.
        },
    }

    return .{
        .device = dev,
        .command_queue = command_queue.?,
        .fence = fence.?,
        .fence_value = 0,
        .fence_event = fence_event,
        .swap_chain = swap_chain,
        .dcomp_device = dcomp_device_ptr,
        .dcomp_target = dcomp_target_ptr,
        .dcomp_visual = dcomp_visual_ptr,
    };
}

pub fn deinit(self: *Device) void {
    // Wait for GPU to finish before releasing anything.
    self.waitForGpu() catch {};

    _ = d3d12.CloseHandle(self.fence_event);
    _ = self.fence.Release();
    _ = self.command_queue.Release();

    if (self.dcomp_visual) |v| _ = v.Release();
    if (self.dcomp_target) |t| _ = t.Release();
    if (self.dcomp_device) |d| _ = d.Release();
    if (self.swap_chain) |sc| _ = sc.Release();

    _ = self.device.Release();

    self.* = undefined;
}

/// Signal the fence from the command queue and block until the GPU catches up.
pub fn waitForGpu(self: *Device) !void {
    self.fence_value += 1;
    const signal_value = self.fence_value;

    var hr = self.command_queue.Signal(self.fence, signal_value);
    if (FAILED(hr)) return error.FenceSignalFailed;

    if (self.fence.GetCompletedValue() < signal_value) {
        hr = self.fence.SetEventOnCompletion(signal_value, self.fence_event);
        if (FAILED(hr)) return error.FenceSetEventFailed;
        _ = d3d12.WaitForSingleObject(self.fence_event, d3d12.INFINITE);
    }
}

// ---- Private helpers ----

fn enableDebugLayer() void {
    var debug: ?*d3d12.ID3D12Debug = null;
    const hr = d3d12.D3D12GetDebugInterface(
        &d3d12.ID3D12Debug.IID,
        @ptrCast(&debug),
    );
    if (SUCCEEDED(hr)) {
        if (debug) |d| {
            d.EnableDebugLayer();
            _ = d.Release();
            log.info("D3D12 debug layer enabled", .{});
        }
    } else {
        log.warn("D3D12 debug layer not available: 0x{x}", .{@as(u32, @bitCast(hr))});
    }
}

fn createCompositionSwapChain(
    factory: *dxgi.IDXGIFactory2,
    queue: *d3d12.ID3D12CommandQueue,
    width: u32,
    height: u32,
) !*dxgi.IDXGISwapChain1 {
    // DXGI rejects 0-dimension swap chains.
    const actual_width = @max(width, 1);
    const actual_height = @max(height, 1);

    const desc = dxgi.DXGI_SWAP_CHAIN_DESC1{
        .Width = actual_width,
        .Height = actual_height,
        .Format = .B8G8R8A8_UNORM,
        .Stereo = 0,
        .SampleDesc = .{ .Count = 1, .Quality = 0 },
        .BufferUsage = dxgi.DXGI_USAGE_RENDER_TARGET_OUTPUT,
        .BufferCount = frame_count,
        // STRETCH causes DirectComposition to interpolate stale content
        // into the bigger area for one frame, which is preferable to
        // the black bar NONE produces with the
        // CreateSwapChainForComposition path. The bounded one-frame
        // stretch artifact is acceptable: setTargetSize wakes the
        // renderer thread immediately, and the 120 Hz draw timer is a
        // backstop, so the renderer typically converges within one
        // frame. If the renderer thread ever stalls (TDR recovery, slow
        // GPU) the stretch becomes a visible smear -- accept that as a
        // graceful degradation rather than a black bar.
        .Scaling = .STRETCH,
        .SwapEffect = .FLIP_DISCARD,
        .AlphaMode = .PREMULTIPLIED,
        .Flags = 0,
    };

    var swap_chain: ?*dxgi.IDXGISwapChain1 = null;
    // DX12 passes the command queue (not the device) to swap chain creation.
    const hr = factory.CreateSwapChainForComposition(
        @ptrCast(queue),
        &desc,
        null,
        &swap_chain,
    );
    if (FAILED(hr)) {
        log.err("CreateSwapChainForComposition failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.SwapChainCreationFailed;
    }
    return swap_chain.?;
}

fn createDCompDevice() !*dcomp.IDCompositionDevice {
    var dcomp_dev: ?*dcomp.IDCompositionDevice = null;
    // Pass null for the DXGI device -- DirectComposition creates its own.
    const hr = dcomp.DCompositionCreateDevice(
        null,
        &dcomp.IDCompositionDevice.IID,
        @ptrCast(&dcomp_dev),
    );
    if (FAILED(hr)) {
        log.err("DCompositionCreateDevice failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.DCompDeviceCreationFailed;
    }
    return dcomp_dev.?;
}

fn createDCompTarget(
    dcomp_dev: *dcomp.IDCompositionDevice,
    hwnd: dxgi.HWND,
) !*dcomp.IDCompositionTarget {
    var target: ?*dcomp.IDCompositionTarget = null;
    const hr = dcomp_dev.CreateTargetForHwnd(hwnd, 1, &target);
    if (FAILED(hr)) {
        log.err("CreateTargetForHwnd failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.DCompTargetCreationFailed;
    }
    return target.?;
}

fn createDCompVisual(
    dcomp_dev: *dcomp.IDCompositionDevice,
    swap_chain: *dxgi.IDXGISwapChain1,
) !*dcomp.IDCompositionVisual {
    var visual: ?*dcomp.IDCompositionVisual = null;
    var hr = dcomp_dev.CreateVisual(&visual);
    if (FAILED(hr)) {
        log.err("CreateVisual failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.DCompVisualCreationFailed;
    }
    errdefer _ = visual.?.Release();

    // Bind the swap chain as content of the visual.
    hr = visual.?.SetContent(@ptrCast(swap_chain));
    if (FAILED(hr)) {
        log.err("IDCompositionVisual.SetContent failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.DCompSetContentFailed;
    }

    return visual.?;
}

// --- Tests ---

test "Device struct fields" {
    // Compile-time check that the struct has the expected fields.
    try std.testing.expect(@hasField(Device, "device"));
    try std.testing.expect(@hasField(Device, "command_queue"));
    try std.testing.expect(@hasField(Device, "fence"));
    try std.testing.expect(@hasField(Device, "fence_value"));
    try std.testing.expect(@hasField(Device, "fence_event"));
    try std.testing.expect(@hasField(Device, "swap_chain"));
}

test "frame_count is 3" {
    try std.testing.expectEqual(@as(u32, 3), frame_count);
}
