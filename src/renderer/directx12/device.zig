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
const global = @import("../../global.zig");
const d3d12 = @import("d3d12.zig");
const dcomp = @import("dcomp.zig");
const dxgi = @import("dxgi.zig");
const Retirement = @import("retire.zig").Retirement;

const GUID = com.GUID;
const HRESULT = com.HRESULT;
const SUCCEEDED = com.SUCCEEDED;
const FAILED = com.FAILED;

const log = std.log.scoped(.directx12);

/// Same scope as `Surface.zig`'s and `DirectX12.zig`'s `init_log`; shared
/// name (not a shared declaration) so the C# host's log bridge files every
/// `surface_init` phase, whichever module logs it, under one
/// `Ghostty.Zig.surface_init` category.
const init_log = std.log.scoped(.surface_init);

/// Number of back buffers (triple buffering).
pub const frame_count: u32 = 3;

// --- Warmup: process-wide warm device handoff ---
//
// D3D12 devices are singletons per adapter per process: D3D12CreateDevice
// returns the existing device (AddRef'd) while any reference to it is
// alive anywhere in the process, and costs 1.2-2.0s cold vs ~1ms once
// warm. `warmup()` pays that cost on a background thread at app startup
// (see App.zig's renderer.Renderer.API.warmup hook and DirectX12.zig's
// forwarder) so the UI thread's first `Device.init` -- which otherwise
// eats almost all of Surface.init's ~440ms of ~520ms measured cost --
// finds the singleton already warm.
//
// The warmup device is deliberately never released by `warmup()` itself:
// releasing the only reference throws the warm state away and the next
// create pays the full cost again. Ownership transfers to the first real
// `Device.init` instead, which takes this slot's reference right after
// its own D3D12CreateDevice call succeeds (see there for why the order
// matters) and releases it once. A reference that lands after the last
// surface is gone stays parked until process exit on purpose: it keeps
// the singleton warm, and a live-object report at shutdown will show it.

/// Guards `warm_device` for cross-thread handoff: `warmup()`'s background
/// thread stores, `Device.init` on the UI thread takes. Same primitive as
/// `shared_texture_mutex` above.
var warm_device_mutex: std.Io.Mutex = .init;

/// Populated by `warmup()`, consumed exactly once by `takeWarmDevice()`.
var warm_device: ?*d3d12.ID3D12Device = null;

/// Create a D3D12 device ahead of time and park it in `warm_device` for
/// the first real `Device.init` to pick up. Safe to call from any thread;
/// never panics, logs and returns on any failure so a warmup problem
/// never blocks the real (synchronous, on-the-critical-path) device
/// creation that follows it.
pub fn warmup() void {
    const started: std.Io.Timestamp = .now(global.io(), .awake);

    // Must match Device.init: the debug layer can only be enabled before
    // the first device is created in the process, so if warmup creates
    // that first device it has to turn the layer on itself, or the real
    // device created later would silently inherit a layer-less singleton.
    if (comptime builtin.mode == .Debug) {
        enableDebugLayer();
    }

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
            log.warn("warmup: CreateDXGIFactory2 failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return;
        }
    }
    defer _ = factory.?.Release();

    var device: ?*d3d12.ID3D12Device = null;
    {
        const hr = d3d12.D3D12CreateDevice(
            null,
            d3d12.D3D_FEATURE_LEVEL_12_0,
            &d3d12.ID3D12Device.IID,
            @ptrCast(&device),
        );
        if (FAILED(hr)) {
            log.warn("warmup: D3D12CreateDevice failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return;
        }
    }
    const dev = device.?;

    // The first command queue created on a device pays extra one-time
    // driver setup cost; create and release one now so it's not paid
    // again by the real command queue Device.init creates later. Same
    // rationale as Metal.warmup's throwaway command queue.
    {
        const desc = d3d12.D3D12_COMMAND_QUEUE_DESC{
            .Type = .DIRECT,
            .Priority = 0,
            .Flags = .NONE,
            .NodeMask = 0,
        };
        var queue: ?*d3d12.ID3D12CommandQueue = null;
        const hr = dev.CreateCommandQueue(
            &desc,
            &d3d12.ID3D12CommandQueue.IID,
            @ptrCast(&queue),
        );
        if (SUCCEEDED(hr)) {
            _ = queue.?.Release();
        } else {
            log.warn("warmup: CreateCommandQueue failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        }
    }

    // Keep the reference alive: store it for Device.init to take. Do not
    // release `dev` here (see the section doc comment above). A reference
    // already parked (a second warmup in the same process) is released
    // rather than overwritten, so the slot never hides a lost AddRef.
    warm_device_mutex.lockUncancelable(global.io());
    if (warm_device) |prev| {
        log.warn("warmup: slot already held a device, releasing the older one", .{});
        _ = prev.Release();
    }
    warm_device = dev;
    warm_device_mutex.unlock(global.io());

    init_log.info(
        "surface_init warmup-device done +{d} ms",
        .{started.untilNow(global.io(), .awake).toMilliseconds()},
    );
}

/// Take and clear the warm device slot, if populated. Called by
/// `Device.init` right after its own D3D12CreateDevice succeeds.
fn takeWarmDevice() ?*d3d12.ID3D12Device {
    warm_device_mutex.lockUncancelable(global.io());
    defer warm_device_mutex.unlock(global.io());
    const dev = warm_device;
    warm_device = null;
    return dev;
}

/// Release a still-parked warm reference, if any. Device.init calls this
/// right after its own create succeeds (the handoff); device recovery
/// calls it before tearing the lost device down, because a parked
/// reference to a removed device would keep the singleton alive and make
/// the recovery's D3D12CreateDevice hand the removed device straight back.
pub fn dropWarmDevice() void {
    if (takeWarmDevice()) |warm| _ = warm.Release();
}

// --- Device state ---

device: *d3d12.ID3D12Device,
command_queue: *d3d12.ID3D12CommandQueue,
fence: *d3d12.ID3D12Fence,
fence_value: std.atomic.Value(u64),
fence_event: std.os.windows.HANDLE,

/// Resources whose owners are done with them but which the GPU may still
/// be reading. D3D12 does not retain what a submitted command list
/// references, so a buffer or texture cannot be released the moment its
/// owner drops it -- only once `fence` has reached the value its last
/// referencing submission was signaled with. Everything that would
/// otherwise call `Release` on a resource the renderer draws from goes
/// through here instead.
///
/// Heap-allocated because `Device` is copied by value into `DirectX12`,
/// which the generic renderer in turn passes around by value: buffers and
/// textures capture this pointer in their options at creation time and
/// must all reach the same queue. Same reason the descriptor heaps in
/// DirectX12.zig are pointers.
retirement: *Retirement,

swap_chain: ?*dxgi.IDXGISwapChain1,

/// The DXGI_SWAP_CHAIN_FLAG_* bits `swap_chain` was created with. A
/// ResizeBuffers call must pass the same set or DXGI rejects it with
/// DXGI_ERROR_INVALID_CALL, so DirectX12.resizeSwapChain reads this
/// instead of recomputing a flag list that could drift from creation
/// (it did drift, twice, during the rc.8 prototype: creation carried a
/// flag the panel path's entry point rejects, and the whole surface
/// never came up -- see compositionSwapChainDesc).
swap_chain_flags: u32 = 0,

/// QueryInterface of `swap_chain` for the IDXGISwapChain2 surface:
/// frame-latency waitable, maximum frame latency, matrix transform.
/// Owned by Device (Release in deinit), acquired in init and re-acquired
/// on every swap chain recreation (TDR recovery goes back through
/// Device.init). Null when there is no swap chain or the QI fails, in
/// which case the renderer runs un-paced and un-transformed, as before.
swap_chain2: ?*dxgi.IDXGISwapChain2 = null,

/// The frame-latency waitable from GetFrameLatencyWaitableObject. NOT
/// owned: the swap chain closes it, so it lives and dies with the swap
/// chain object and must be re-read whenever the swap chain is created.
/// The renderer waits it at most once per Present (auto-reset event;
/// see DirectX12.beginFrame).
frame_latency_waitable: ?std.os.windows.HANDLE = null,

/// DirectComposition surface handle backing the swap chain in
/// SwapChainPanel mode. Owned by Device: created in init, closed in
/// deinit. The embedder retrieves it via
/// ghostty_surface_get_swap_chain_handle and binds it to the panel with
/// ISwapChainPanelNative2::SetSwapChainHandle. Null in all other modes.
swap_chain_surface_handle: ?std.os.windows.HANDLE = null,

// DirectComposition objects, only used for HWND surfaces.
dcomp_device: ?*dcomp.IDCompositionDevice,
dcomp_target: ?*dcomp.IDCompositionTarget,
dcomp_visual: ?*dcomp.IDCompositionVisual,

/// Null for HWND / SwapChainPanel modes. Readers must hold
/// shared_texture_mutex.
shared_texture: ?SharedTextureState = null,

/// Guards shared_texture for atomic snapshot reads by
/// ghostty_surface_shared_texture() on the apprt thread.
shared_texture_mutex: std.Io.Mutex = .init,

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
    /// by Device. Survives resize; a device-removed recovery replaces
    /// the fence and so this handle too, under a bumped `version`.
    fence_handle: std.os.windows.HANDLE,
    /// Pixel dimensions of `resource`.
    width: u32,
    height: u32,
    /// Monotonically increasing; bumped by recreateSharedTexture and
    /// carried across a device recreation so it never goes backwards.
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
    /// SwapChainPanel mode only: bind the new swap chain to this existing
    /// DirectComposition surface handle instead of minting one. The handle
    /// outlives the D3D12 device that presented into it, so a device
    /// recreated after a TDR can keep presenting into the surface the
    /// embedder already bound with SetSwapChainHandle, and the panel never
    /// has to be told. Ownership transfers to the Device on success.
    surface_handle: ?std.os.windows.HANDLE = null,
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

    // Handoff: `dev` above is either a fresh device or (if warmup() won
    // the race) the same singleton object warmup's D3D12CreateDevice
    // call already returned, AddRef'd again for `dev`. Either way `dev`
    // now holds a reference, so it is safe to drop warmup's -- taking it
    // here, right after `dev` is established rather than before, is what
    // keeps the singleton's refcount from ever touching zero in between.
    // A no-op if warmup never ran or hasn't finished yet.
    dropWarmDevice();

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

    const fence_event = d3d12.CreateEventW(null, .FALSE, .FALSE, null) orelse {
        log.err("CreateEventW failed for fence event", .{});
        return error.FenceEventCreationFailed;
    };
    errdefer _ = d3d12.CloseHandle(fence_event);

    // -- Swap chain + composition (surface-dependent) --
    var swap_chain: ?*dxgi.IDXGISwapChain1 = null;
    var swap_chain_flags: u32 = 0;
    var swap_chain_surface_handle: ?std.os.windows.HANDLE = null;
    var dcomp_device_ptr: ?*dcomp.IDCompositionDevice = null;
    var dcomp_target_ptr: ?*dcomp.IDCompositionTarget = null;
    var dcomp_visual_ptr: ?*dcomp.IDCompositionVisual = null;
    var result_shared_texture: ?SharedTextureState = null;

    switch (surface) {
        .hwnd => |hwnd| {
            // HWND surface: composition swap chain + DirectComposition.
            // DX12 command queues implement IUnknown, which DXGI needs.
            const result = try createCompositionSwapChain(
                factory.?,
                command_queue.?,
                opts.width,
                opts.height,
            );
            swap_chain = result.swap_chain;
            swap_chain_flags = result.flags;
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
        .swap_chain_panel => {
            // SwapChainPanel surface: present into a DirectComposition
            // surface handle rather than binding the swap chain object to
            // the panel directly. The embedder binds the handle via
            // ISwapChainPanelNative2::SetSwapChainHandle. Binding the
            // handle (a stable composition primitive) instead of the swap
            // chain object means DWM composites the panel as soon as the
            // window is shown -- the direct SetSwapChain path could leave
            // presented frames uncomposited until the first OS activation
            // (the blank-until-focus startup race) -- and the binding
            // survives ResizeBuffers without re-binding.
            const result = try createSurfaceHandleSwapChain(
                factory.?,
                command_queue.?,
                opts.width,
                opts.height,
                opts.surface_handle,
            );
            swap_chain = result.swap_chain;
            swap_chain_flags = result.flags;
            swap_chain_surface_handle = result.handle;
        },
        .composition => {
            // Composition surface: create the swap chain but don't bind
            // it to a panel or HWND. The embedder retrieves the pointer
            // via ghostty_surface_get_swap_chain and binds it to a
            // Windows.UI.Composition visual for per-pixel alpha.
            const result = try createCompositionSwapChain(
                factory.?,
                command_queue.?,
                opts.width,
                opts.height,
            );
            swap_chain = result.swap_chain;
            swap_chain_flags = result.flags;
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
    // Defensive: if a future fallible step lands between here and the
    // return, these prevent leaking the surface handle / swap chain (panel
    // mode) and the shared resource + both NT handles (shared-texture
    // mode). swap_chain_surface_handle is only set in panel mode, so this
    // errdefer is a no-op for the other surface variants.
    errdefer if (swap_chain_surface_handle) |h| {
        _ = swap_chain.?.Release();
        // A handle the caller passed in stays the caller's to close.
        if (opts.surface_handle == null) _ = d3d12.CloseHandle(h);
    };
    errdefer if (result_shared_texture) |st| {
        _ = d3d12.CloseHandle(st.fence_handle);
        _ = d3d12.CloseHandle(st.resource_handle);
        _ = st.resource.Release();
    };

    // Frame-latency pacing, where the swap chain was created paced: QI
    // the IDXGISwapChain2 surface (also wanted unpaced, for the matrix
    // transform), cap the queue at one in-flight frame, and take the
    // waitable the renderer waits before each frame. Only chains created
    // with the waitable flag accept SetMaximumFrameLatency and expose a
    // waitable; the panel path's chain is created unpaced (its creation
    // entry point rejects the flag -- see compositionSwapChainDesc), so
    // there pacing is Present(1,0)'s own vblank block and this block
    // only supplies the swap_chain2 pointer. The waitable is re-read on
    // every swap chain creation, which is what makes the TDR-recovery
    // path (deinitGpu + initGpu) get a live one instead of the dead
    // swap chain's handle. A QI failure logs and degrades to the
    // previous behavior rather than failing surface creation:
    // IDXGISwapChain2 has shipped since Windows 8.1, so this is
    // belt-and-braces, not an expected path.
    const paced = (swap_chain_flags &
        dxgi.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT) != 0;
    var swap_chain2: ?*dxgi.IDXGISwapChain2 = null;
    var frame_latency_waitable: ?std.os.windows.HANDLE = null;
    if (swap_chain) |sc| {
        const hr = sc.vtable.QueryInterface(
            @ptrCast(sc),
            &dxgi.IDXGISwapChain2.IID,
            @ptrCast(&swap_chain2),
        );
        if (FAILED(hr)) {
            log.warn(
                "QueryInterface for IDXGISwapChain2 failed: 0x{x}; no matrix transform or pacing",
                .{@as(u32, @bitCast(hr))},
            );
        } else {
            errdefer _ = swap_chain2.?.Release();
            if (paced) {
                const latency_hr = swap_chain2.?.SetMaximumFrameLatency(1);
                if (FAILED(latency_hr)) {
                    log.warn(
                        "SetMaximumFrameLatency(1) failed: 0x{x}",
                        .{@as(u32, @bitCast(latency_hr))},
                    );
                }
                frame_latency_waitable = swap_chain2.?.GetFrameLatencyWaitableObject();
                if (frame_latency_waitable == null) {
                    log.warn("GetFrameLatencyWaitableObject returned null", .{});
                }
            }
        }
    }

    // Deferred-release queue. Backed by the C allocator because Device.init
    // takes no allocator and the buffers/textures that retire into it also
    // reach it from value-receiver deinits.
    const retirement = try std.heap.c_allocator.create(Retirement);
    errdefer std.heap.c_allocator.destroy(retirement);
    retirement.* = .init(std.heap.c_allocator);

    return .{
        .device = dev,
        .command_queue = command_queue.?,
        .fence = fence.?,
        .retirement = retirement,
        .fence_value = std.atomic.Value(u64).init(0),
        .fence_event = fence_event,
        .swap_chain = swap_chain,
        .swap_chain_flags = swap_chain_flags,
        .swap_chain2 = swap_chain2,
        .frame_latency_waitable = frame_latency_waitable,
        .swap_chain_surface_handle = swap_chain_surface_handle,
        .dcomp_device = dcomp_device_ptr,
        .dcomp_target = dcomp_target_ptr,
        .dcomp_visual = dcomp_visual_ptr,
        .shared_texture = result_shared_texture,
    };
}

pub fn deinit(self: *Device) void {
    // Wait for GPU to finish before releasing anything.
    //
    // A failure here is not ignorable the way a swallowed `catch {}`
    // implied: everything below this line final-releases resources the GPU
    // may still be reading, which is the same corruption the retirement
    // queue exists to prevent. The releases below have no alternative --
    // they are owned here and nothing else will free them -- but the
    // reason has to reach the log, and the device-removed reason
    // distinguishes the benign case (the GPU is gone, so nothing is
    // reading anything) from a live device whose fence we failed to
    // signal. What the retirement queue holds DOES have an alternative;
    // see below.
    //
    // A removed device is the benign case made explicit: its queue can no
    // longer signal, so the wait cannot succeed, but nothing on it is
    // executing either, so there is nothing left to drain and every
    // release below is safe. This is the path a device-loss recovery
    // takes, once per lost device, so it must not leak.
    var drained = true;
    if (self.removed()) {
        log.warn(
            "device removed (reason 0x{x}); tearing down without a GPU drain",
            .{@as(u32, @bitCast(self.device.GetDeviceRemovedReason()))},
        );
    } else self.waitForGpu() catch |err| {
        drained = false;
        log.err(
            "waitForGpu before device teardown failed: {}; releasing GPU resources without a drain, and leaking {d} retired resource(s) rather than freeing them under a live GPU",
            .{ err, self.retirement.count() },
        );
    };

    // Free anything the retirement queue is still holding -- but only when
    // the drain above actually succeeded or was unnecessary. Device.deinit
    // runs per surface, on every tab and split close, in a process that
    // keeps running, so "the process is going away" is not available as a
    // justification. On a failed wait against a live device the GPU may
    // still be reading these, and leaking them is strictly safer than
    // releasing them; the back-buffer and frame releases below have no
    // such option, which is why the asymmetry is worth taking here.
    if (drained) self.retirement.drainAll();
    self.retirement.deinit();
    std.heap.c_allocator.destroy(self.retirement);

    _ = d3d12.CloseHandle(self.fence_event);
    _ = self.fence.Release();
    _ = self.command_queue.Release();

    if (self.dcomp_visual) |v| _ = v.Release();
    if (self.dcomp_target) |t| _ = t.Release();
    if (self.dcomp_device) |d| _ = d.Release();
    // swap_chain2 first: it is a QI of swap_chain, and releasing the
    // underlying object through either pointer is the same final release.
    // frame_latency_waitable needs no close: the swap chain owns it.
    if (self.swap_chain2) |sc2| _ = sc2.Release();
    if (self.swap_chain) |sc| _ = sc.Release();
    // Close the composition surface handle after releasing the swap
    // chain that presents into it.
    if (self.swap_chain_surface_handle) |h| _ = d3d12.CloseHandle(h);

    if (self.shared_texture) |st| {
        _ = d3d12.CloseHandle(st.fence_handle);
        _ = d3d12.CloseHandle(st.resource_handle);
        _ = st.resource.Release();
        self.shared_texture = null;
    }

    _ = self.device.Release();

    self.* = undefined;
}

/// Whether the device has been removed (TDR, driver upgrade, adapter
/// gone). Cheap: no GPU round trip, just the driver's recorded reason.
pub fn removed(self: *const Device) bool {
    return FAILED(self.device.GetDeviceRemovedReason());
}

/// Signal the fence from the command queue and block until the GPU catches up.
///
/// On success this is also a full drain of the retirement queue: the signal
/// seals every resource retired since the last submission, and reaching it
/// proves the GPU is done with all of them.
pub fn waitForGpu(self: *Device) !void {
    const signal_value = self.fence_value.fetchAdd(1, .release) + 1;

    var hr = self.command_queue.Signal(self.fence, signal_value);
    if (FAILED(hr)) return error.FenceSignalFailed;
    self.retirement.seal(signal_value);

    if (self.fence.GetCompletedValue() < signal_value) {
        hr = self.fence.SetEventOnCompletion(signal_value, self.fence_event);
        if (FAILED(hr)) return error.FenceSetEventFailed;
        _ = d3d12.WaitForSingleObject(self.fence_event, d3d12.INFINITE);
    }

    self.retirement.collect(self.fence.GetCompletedValue());
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

    self.shared_texture_mutex.lockUncancelable(global.io());
    defer self.shared_texture_mutex.unlock(global.io());

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

/// Build the swap-chain description shared by every composition path
/// (HWND, SwapChainPanel via surface handle, and bare composition).
///
/// `paced` selects the frame-latency waitable flag, and legality decides
/// it, not preference: IDXGIFactoryMedia::CreateSwapChainForComposition
/// SurfaceHandle -- the only entry point that can target a DComposition
/// surface handle, i.e. the app's production SwapChainPanel binding --
/// was measured rejecting a nonzero Flags value with
/// DXGI_ERROR_INVALID_CALL (0x887a0001): every creation retry failed
/// under both scaling values until Flags went to 0, an app that never
/// rendered and an empty window. Honesty note on that measurement: it
/// was taken while the flag constant in dxgi.zig carried 16, which the
/// SDK calls RESTRICT_SHARED_RESOURCE_DRIVER, not the waitable 64 -- so
/// what is directly measured is "this entry point rejects that nonzero
/// flag", and the panel path staying unpaced (Flags = 0) is the
/// conservative reading that holds under either value.
/// IDXGIFactory2::CreateSwapChainForComposition accepting the true
/// waitable flag rests on the Windows Terminal precedent and the DXGI
/// docs, not on a run in this tree. Callers must pass the
/// same flag decision to every ResizeBuffers on the chain they built
/// (Device carries it in swap_chain_flags for exactly that).
fn compositionSwapChainDesc(width: u32, height: u32, paced: bool) dxgi.DXGI_SWAP_CHAIN_DESC1 {
    // DXGI rejects 0-dimension swap chains.
    const actual_width = @max(width, 1);
    const actual_height = @max(height, 1);

    return .{
        .Width = actual_width,
        .Height = actual_height,
        .Format = .B8G8R8A8_UNORM,
        .Stereo = 0,
        .SampleDesc = .{ .Count = 1, .Quality = 0 },
        .BufferUsage = dxgi.DXGI_USAGE_RENDER_TARGET_OUTPUT,
        .BufferCount = frame_count,
        // STRETCH, and it must stay STRETCH: DXGI_SCALING_NONE is valid
        // only for CreateSwapChainForHwnd swap chains. Every chain this
        // desc builds is a composition chain (panel via surface handle,
        // bare composition, hwnd-hosted-via-DComp), and DXGI rejects
        // NONE at creation with DXGI_ERROR_INVALID_CALL (0x887a0001) --
        // measured: 132 consecutive creation failures, an app that never
        // rendered, and an empty window on screen. The preceding
        // investigation's Phase A attribution (STRETCH magnifying stale
        // content during a resize) is wrong at this layer: filmed
        // resizes show the stale frame 1:1 top-left with the exposed
        // strip BLACK, never stretched. The strip is the SwapChainPanel
        // area the chain content does not cover; what fills it is
        // addressed by the swap chain background color
        // (DirectX12.setBackgroundColor) and the panel host, not by
        // this field. XAML's panel applies its own display scale to a
        // handle-bound swap chain; the counter-transform that undoes it
        // (SetMatrixTransform 1/scale, the Windows Terminal
        // AtlasEngine pattern) is applied by DirectX12 alongside the
        // background color.
        .Scaling = .STRETCH,
        // FLIP_SEQUENTIAL is required for premultiplied alpha to
        // composite correctly through SwapChainPanel. FLIP_DISCARD
        // may discard back buffer contents between presents, breaking
        // premultiplied alpha compositing through DWM.
        .SwapEffect = .FLIP_SEQUENTIAL,
        .AlphaMode = .PREMULTIPLIED,
        // The frame-latency waitable caps the present queue (paired
        // with SetMaximumFrameLatency(1) and a wait before each frame)
        // on the paths whose creation entry point accepts the flag, so
        // a new-size frame composes on the first vblank after its
        // Present instead of behind up to two stale frames. Only legal
        // with the factory2 creation path -- see the function comment.
        .Flags = if (paced)
            dxgi.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT
        else
            0,
    };
}

fn createCompositionSwapChain(
    factory: *dxgi.IDXGIFactory2,
    queue: *d3d12.ID3D12CommandQueue,
    width: u32,
    height: u32,
) !CompositionSwapChain {
    // Factory2 composition creation accepts the waitable flag.
    const desc = compositionSwapChainDesc(width, height, true);

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
    return .{ .swap_chain = swap_chain.?, .flags = desc.Flags };
}

/// A swap chain plus the DXGI flags it was created with, so every later
/// ResizeBuffers on it can repeat them (DXGI_ERROR_INVALID_CALL on a
/// mismatch). See compositionSwapChainDesc for how `flags` is decided.
const CompositionSwapChain = struct {
    swap_chain: *dxgi.IDXGISwapChain1,
    flags: u32,
};

const SurfaceHandleSwapChain = struct {
    swap_chain: *dxgi.IDXGISwapChain1,
    handle: std.os.windows.HANDLE,
    flags: u32,
};

/// Create a composition swap chain bound to a DirectComposition surface
/// handle, minting the handle unless the caller passes one in. The caller
/// owns both on return: Release the swap chain and CloseHandle the handle.
/// Used by SwapChainPanel mode so the embedder can bind the handle via
/// ISwapChainPanelNative2::SetSwapChainHandle.
///
/// The embedder binds the handle to the panel exactly once after surface
/// creation, and the handle is a composition primitive that does not
/// belong to any D3D12 device. That is what lets a device recreated after
/// a TDR present into the panel without the embedder rebinding: it passes
/// the old handle in `existing` and gets a new swap chain on the same
/// surface. On failure a passed-in handle stays the caller's to close.
fn createSurfaceHandleSwapChain(
    factory: *dxgi.IDXGIFactory2,
    queue: *d3d12.ID3D12CommandQueue,
    width: u32,
    height: u32,
    existing: ?std.os.windows.HANDLE,
) !SurfaceHandleSwapChain {
    // The composition-surface-handle entry point lives on IDXGIFactoryMedia,
    // which the factory we already created supports via QueryInterface.
    var media: ?*dxgi.IDXGIFactoryMedia = null;
    {
        const hr = factory.vtable.QueryInterface(
            factory,
            &dxgi.IDXGIFactoryMedia.IID,
            @ptrCast(&media),
        );
        if (FAILED(hr)) {
            log.err("QueryInterface for IDXGIFactoryMedia failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return error.FactoryMediaQueryFailed;
        }
    }
    defer _ = media.?.Release();

    const handle: std.os.windows.HANDLE = existing orelse minted: {
        var fresh: std.os.windows.HANDLE = undefined;
        const hr = dcomp.DCompositionCreateSurfaceHandle(
            dcomp.COMPOSITIONOBJECT_ALL_ACCESS,
            null,
            &fresh,
        );
        if (FAILED(hr)) {
            log.err("DCompositionCreateSurfaceHandle failed: 0x{x}", .{@as(u32, @bitCast(hr))});
            return error.SurfaceHandleCreationFailed;
        }
        break :minted fresh;
    };
    // Only a handle minted here is ours to close on failure.
    errdefer if (existing == null) {
        _ = d3d12.CloseHandle(handle);
    };

    // Unpaced, and it must stay that way: this entry point rejects the
    // frame-latency waitable flag (see compositionSwapChainDesc). Every
    // FAILED(hr) here used to be preceded by two full sessions of
    // 0x887a0001 retried every 500ms before the flag was isolated.
    const desc = compositionSwapChainDesc(width, height, false);

    var swap_chain: ?*dxgi.IDXGISwapChain1 = null;
    // DX12 passes the command queue (not the device) to swap chain creation.
    const hr = media.?.CreateSwapChainForCompositionSurfaceHandle(
        @ptrCast(queue),
        handle,
        &desc,
        null,
        &swap_chain,
    );
    if (FAILED(hr)) {
        log.err("CreateSwapChainForCompositionSurfaceHandle failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.SwapChainCreationFailed;
    }

    return .{ .swap_chain = swap_chain.?, .handle = handle, .flags = desc.Flags };
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
    try std.testing.expect(@hasField(Device, "swap_chain_flags"));
    try std.testing.expect(@hasField(Device, "swap_chain2"));
    try std.testing.expect(@hasField(Device, "frame_latency_waitable"));
}

test "composition swap chain desc: STRETCH scaling, flag legality" {
    // Pinned where they are set. STRETCH is not a style choice: every
    // chain this desc builds is a composition chain, and DXGI rejects
    // DXGI_SCALING_NONE at creation with DXGI_ERROR_INVALID_CALL
    // (0x887a0001, measured on this app's panel path: the surface never
    // initialized). The waitable flag is decided by the CREATION ENTRY
    // POINT, not preference: legal on CreateSwapChainForComposition,
    // rejected (same 0x887a0001, also measured) by the
    // CreateSwapChainForCompositionSurfaceHandle path the SwapChainPanel
    // binding must use. If either half regresses, the app either fails
    // to create its swap chain or paces a chain that has no waitable.
    const paced = compositionSwapChainDesc(640, 480, true);
    try std.testing.expectEqual(dxgi.DXGI_SCALING.STRETCH, paced.Scaling);
    try std.testing.expectEqual(
        dxgi.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
        paced.Flags,
    );
    // The literals are load-bearing: an earlier draft of the dxgi.zig
    // constant carried 16 (the SDK's RESTRICT_SHARED_RESOURCE_DRIVER),
    // every pacing call then failed silently, and a test comparing the
    // constant against itself stayed green the whole time. Pin the ABI
    // value (dxgi.h, 10.0.26100.0) so that cannot recur.
    try std.testing.expectEqual(@as(u32, 0x40), paced.Flags);
    try std.testing.expectEqual(
        @as(u32, 0x40),
        dxgi.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
    );
    try std.testing.expectEqual(frame_count, paced.BufferCount);
    try std.testing.expectEqual(dxgi.DXGI_ALPHA_MODE.PREMULTIPLIED, paced.AlphaMode);

    const unpaced = compositionSwapChainDesc(640, 480, false);
    try std.testing.expectEqual(@as(u32, 0), unpaced.Flags);
    try std.testing.expectEqual(dxgi.DXGI_SCALING.STRETCH, unpaced.Scaling);
}

test "frame_count is 3" {
    try std.testing.expectEqual(@as(u32, 3), frame_count);
}

test "warm device slot: take clears it, empty take returns null" {
    // Exercises the handoff bookkeeping without touching D3D: park a
    // never-dereferenced pointer directly in the module-level slot, the
    // same way warmup() would, and confirm takeWarmDevice hands it back
    // exactly once and leaves the slot empty for the next caller.
    var fake: d3d12.ID3D12Device = undefined;

    warm_device_mutex.lockUncancelable(global.io());
    warm_device = &fake;
    warm_device_mutex.unlock(global.io());

    try std.testing.expectEqual(@as(?*d3d12.ID3D12Device, &fake), takeWarmDevice());
    try std.testing.expectEqual(@as(?*d3d12.ID3D12Device, null), takeWarmDevice());
}
