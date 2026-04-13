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
fence_value: std.atomic.Value(u64),
fence_event: std.os.windows.HANDLE,

swap_chain: ?*dxgi.IDXGISwapChain1,

// DirectComposition objects, only used for HWND surfaces.
dcomp_device: ?*dcomp.IDCompositionDevice,
dcomp_target: ?*dcomp.IDCompositionTarget,
dcomp_visual: ?*dcomp.IDCompositionVisual,

/// Null for HWND / SwapChainPanel modes. Readers must hold
/// shared_texture_mutex.
shared_texture: ?SharedTextureState = null,

/// Guards shared_texture for atomic snapshot reads by
/// ghostty_surface_shared_texture() on the apprt thread.
shared_texture_mutex: std.Thread.Mutex = .{},

/// Shared-texture mode state. Populated by Device.init when the
/// surface variant is .shared_texture, torn down in Device.deinit,
/// and recreated on resize.
pub const SharedTextureState = struct {
    /// The ID3D12Resource ghostty renders into. Owned by Device.
    resource: *d3d12.ID3D12Resource,
    /// NT HANDLE from CreateSharedHandle on `resource`. Owned by
    /// Device. Closed and reborn on resize.
    resource_handle: std.os.windows.HANDLE,
    /// NT HANDLE from CreateSharedHandle on the Device's fence. Owned
    /// by Device. Stable for the surface lifetime.
    fence_handle: std.os.windows.HANDLE,
    /// Pixel dimensions of `resource`.
    width: u32,
    height: u32,
    /// Monotonically increasing; bumped by recreateSharedTexture.
    version: u64,

    /// Create a shared committed ID3D12Resource and NT handles for both
    /// the resource and the given fence. Returns a populated
    /// SharedTextureState ready to be stored on Device.
    ///
    /// Format is B8G8R8A8_UNORM to match the renderer's swap-chain path
    /// (no shader or pipeline permutations required). Flags are
    /// ALLOW_RENDER_TARGET (ghostty writes to it) plus
    /// ALLOW_SIMULTANEOUS_ACCESS (consumers can read while ghostty writes
    /// without explicit state transitions). Initial state is COMMON, the
    /// only state ALLOW_SIMULTANEOUS_ACCESS resources are ever allowed to
    /// be in on either device -- the fence is our sole synchronization
    /// primitive.
    ///
    /// Width/height are clamped to a minimum of 1 because
    /// CreateCommittedResource rejects zero dimensions.
    pub fn init(
        device: *d3d12.ID3D12Device,
        fence: *d3d12.ID3D12Fence,
        width: u32,
        height: u32,
    ) !SharedTextureState {
        const w: u32 = @max(width, 1);
        const h: u32 = @max(height, 1);

        const heap_props = d3d12.D3D12_HEAP_PROPERTIES{
            .Type = .DEFAULT,
            .CPUPageProperty = 0,
            .MemoryPoolPreference = 0,
            .CreationNodeMask = 0,
            .VisibleNodeMask = 0,
        };

        const desc = d3d12.D3D12_RESOURCE_DESC{
            .Dimension = .TEXTURE2D,
            .Alignment = 0,
            .Width = @as(u64, w),
            .Height = h,
            .DepthOrArraySize = 1,
            .MipLevels = 1,
            .Format = .B8G8R8A8_UNORM,
            .SampleDesc = .{ .Count = 1, .Quality = 0 },
            .Layout = .UNKNOWN,
            .Flags = @enumFromInt(
                @intFromEnum(d3d12.D3D12_RESOURCE_FLAGS.ALLOW_RENDER_TARGET) |
                    @intFromEnum(d3d12.D3D12_RESOURCE_FLAGS.ALLOW_SIMULTANEOUS_ACCESS),
            ),
        };

        var resource: ?*d3d12.ID3D12Resource = null;
        {
            const hr = device.CreateCommittedResource(
                &heap_props,
                @intFromEnum(d3d12.D3D12_HEAP_FLAGS.SHARED),
                &desc,
                .COMMON,
                null,
                &d3d12.ID3D12Resource.IID,
                @ptrCast(&resource),
            );
            if (FAILED(hr)) {
                log.err("CreateCommittedResource (shared) failed: 0x{x}", .{@as(u32, @bitCast(hr))});
                return error.SharedResourceCreationFailed;
            }
        }
        const res = resource orelse return error.SharedResourceCreationFailed;
        errdefer _ = res.Release();

        var resource_handle: std.os.windows.HANDLE = undefined;
        {
            const hr = device.CreateSharedHandle(
                @ptrCast(res),
                d3d12.GENERIC_ALL,
                &resource_handle,
            );
            if (FAILED(hr)) {
                log.err("CreateSharedHandle (resource) failed: 0x{x}", .{@as(u32, @bitCast(hr))});
                return error.SharedHandleCreationFailed;
            }
        }
        errdefer _ = d3d12.CloseHandle(resource_handle);

        var fence_handle: std.os.windows.HANDLE = undefined;
        {
            const hr = device.CreateSharedHandle(
                @ptrCast(fence),
                d3d12.GENERIC_ALL,
                &fence_handle,
            );
            if (FAILED(hr)) {
                log.err("CreateSharedHandle (fence) failed: 0x{x}", .{@as(u32, @bitCast(hr))});
                return error.SharedHandleCreationFailed;
            }
        }
        errdefer _ = d3d12.CloseHandle(fence_handle);

        return .{
            .resource = res,
            .resource_handle = resource_handle,
            .fence_handle = fence_handle,
            .width = w,
            .height = h,
            .version = 1,
        };
    }
};

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

    // Fence must be created with SHARED flag when we will later call
    // CreateSharedHandle on it (shared-texture mode). The flag is
    // noise for the HWND/SwapChainPanel paths but would prevent the
    // debug layer from warning about attempting to share an unshared
    // fence, and CreateFence itself does not care either way.
    const fence_flags: d3d12.D3D12_FENCE_FLAGS = switch (surface) {
        .hwnd, .swap_chain_panel, .composition => .NONE,
        .shared_texture => .SHARED,
    };

    // -- Fence --
    var fence: ?*d3d12.ID3D12Fence = null;
    {
        const hr = dev.CreateFence(
            0,
            fence_flags,
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
    var result_shared_texture: ?SharedTextureState = null;

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
        .composition => {
            // Composition surface: create the swap chain but don't bind
            // it to a panel or HWND. The embedder retrieves the pointer
            // via ghostty_surface_get_swap_chain and binds it to a
            // Windows.UI.Composition visual for per-pixel alpha.
            swap_chain = try createCompositionSwapChain(
                factory.?,
                command_queue.?,
                opts.width,
                opts.height,
            );
            errdefer _ = swap_chain.?.Release();
        },
        .shared_texture => |cfg| {
            // SharedTexture: no swap chain. Create the shared committed
            // resource + NT handles for the resource and the fence so the
            // consumer device can OpenSharedHandle on both.
            result_shared_texture = try SharedTextureState.init(
                dev,
                fence.?,
                cfg.width,
                cfg.height,
            );
        },
    }
    // If anything between here and the final `return` ever gains a
    // fallible step, this errdefer tears down the shared texture state
    // instead of leaking the resource and both NT handles. Today it is
    // defensive -- nothing after this point can fail -- but the cost
    // is nil and the alternative is a latent leak waiting for the next
    // edit.
    errdefer if (result_shared_texture) |st| {
        _ = d3d12.CloseHandle(st.fence_handle);
        _ = d3d12.CloseHandle(st.resource_handle);
        _ = st.resource.Release();
    };

    return .{
        .device = dev,
        .command_queue = command_queue.?,
        .fence = fence.?,
        .fence_value = std.atomic.Value(u64).init(0),
        .fence_event = fence_event,
        .swap_chain = swap_chain,
        .dcomp_device = dcomp_device_ptr,
        .dcomp_target = dcomp_target_ptr,
        .dcomp_visual = dcomp_visual_ptr,
        .shared_texture = result_shared_texture,
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

    if (self.shared_texture) |st| {
        _ = d3d12.CloseHandle(st.fence_handle);
        _ = d3d12.CloseHandle(st.resource_handle);
        _ = st.resource.Release();
        self.shared_texture = null;
    }

    _ = self.device.Release();

    self.* = undefined;
}

/// Signal the fence from the command queue and block until the GPU catches up.
pub fn waitForGpu(self: *Device) !void {
    const signal_value = self.fence_value.fetchAdd(1, .release) + 1;

    var hr = self.command_queue.Signal(self.fence, signal_value);
    if (FAILED(hr)) return error.FenceSignalFailed;

    if (self.fence.GetCompletedValue() < signal_value) {
        hr = self.fence.SetEventOnCompletion(signal_value, self.fence_event);
        if (FAILED(hr)) return error.FenceSetEventFailed;
        _ = d3d12.WaitForSingleObject(self.fence_event, d3d12.INFINITE);
    }
}

/// Recreate the shared texture resource and its NT handle at a new
/// size. Called by the renderer thread on resize. Blocks on
/// waitForGpu to let any in-flight frame referencing the old
/// resource drain, then swaps the state under shared_texture_mutex
/// so ghostty_surface_shared_texture() readers observe either the
/// old or new snapshot, never a mix.
///
/// The fence handle is preserved across resize -- the fence itself
/// is stable for the surface lifetime, and re-issuing a shared
/// handle for it would force every consumer to re-open the fence
/// every time the window resized. SharedTextureState.init
/// unavoidably produces a fresh fence handle as part of its output;
/// we close it immediately.
///
/// Returns error.NotSharedTextureMode if the surface is not in
/// shared-texture mode (programmer error -- the renderer should not
/// call this for HWND or SwapChainPanel surfaces).
pub fn recreateSharedTexture(self: *Device, width: u32, height: u32) !void {
    // Drain GPU work referencing the old resource before releasing
    // anything it might still touch. Log on failure so a TDR mid-
    // resize leaves a trail in the renderer log; the caller still
    // has to set device_lost, but at least the diagnostic is here.
    self.waitForGpu() catch |err| {
        log.err("waitForGpu failed during recreateSharedTexture: {}", .{err});
        return err;
    };

    // Build a fresh state off-lock. SharedTextureState.init does
    // its own errdefer cleanup on failure, so nothing leaks if this
    // returns an error.
    const new_state = try SharedTextureState.init(
        self.device,
        self.fence,
        width,
        height,
    );

    // Fence handle is stable across resize; discard the new one
    // (see doc comment above).
    _ = d3d12.CloseHandle(new_state.fence_handle);

    self.shared_texture_mutex.lock();
    defer self.shared_texture_mutex.unlock();

    const old = self.shared_texture orelse {
        // Caller violated the contract: this method only makes
        // sense in shared-texture mode. Clean up the new state we
        // just built before reporting the error.
        _ = d3d12.CloseHandle(new_state.resource_handle);
        _ = new_state.resource.Release();
        return error.NotSharedTextureMode;
    };

    const next_version = old.version + 1;

    // Swap. Close the old resource handle and release the old
    // resource only AFTER the new state is fully staged, so any
    // failure path above leaves the old state intact.
    _ = d3d12.CloseHandle(old.resource_handle);
    _ = old.resource.Release();

    self.shared_texture = .{
        .resource = new_state.resource,
        .resource_handle = new_state.resource_handle,
        // Preserved from the old state -- fence handle is stable.
        .fence_handle = old.fence_handle,
        .width = new_state.width,
        .height = new_state.height,
        .version = next_version,
    };
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
        // FLIP_SEQUENTIAL is required for premultiplied alpha to
        // composite correctly through SwapChainPanel. FLIP_DISCARD
        // may discard back buffer contents between presents, breaking
        // premultiplied alpha compositing through DWM.
        .SwapEffect = .FLIP_SEQUENTIAL,
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
