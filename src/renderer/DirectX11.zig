//! Graphics API wrapper for DirectX 11.
//!
//! This module provides the GraphicsAPI contract required by GenericRenderer,
//! mirroring the structure of Metal.zig and OpenGL.zig.
//!
//! Current status: device init, shader loading, and render loop wiring are
//! functional. The bg_color pipeline renders the terminal background through
//! the real Frame -> RenderPass -> step() path.
pub const DirectX11 = @This();

const builtin = @import("builtin");
const std = @import("std");
const Allocator = std.mem.Allocator;

const configpkg = @import("../config.zig");
const font = @import("../font/main.zig");
const rendererpkg = @import("../renderer.zig");
const Renderer = rendererpkg.GenericRenderer(DirectX11);
const shadertoy = @import("shadertoy.zig");
const apprt = @import("../apprt.zig");
const log = std.log.scoped(.directx11);

// --- GraphicsAPI contract: types ---

pub const GraphicsAPI = DirectX11;
pub const Target = @import("directx11/Target.zig");
pub const Frame = @import("directx11/Frame.zig");
pub const RenderPass = @import("directx11/RenderPass.zig");
pub const Pipeline = @import("directx11/Pipeline.zig");
pub const Sampler = @import("directx11/Sampler.zig");
pub const Texture = @import("directx11/Texture.zig");

const bufferpkg = @import("directx11/buffer.zig");
pub const Buffer = bufferpkg.Buffer;

pub const shaders = @import("directx11/shaders.zig");

// TODO: custom shaders not yet supported on DX11. Using .glsl as placeholder;
// DX11 will need its own shadertoy.Target variant (.hlsl) when custom shaders
// are implemented. Can't add it without modifying upstream shadertoy.zig.
pub const custom_shader_target: shadertoy.Target = .glsl;

/// DX11 uses top-left origin, same as Metal.
pub const custom_shader_y_is_down = true;

/// Standard DXGI double-buffering.
pub const swap_chain_count = 2;

/// Pixel format for image texture options.
pub const ImageTextureFormat = enum {
    /// 1 byte per pixel grayscale.
    gray,
    /// 4 bytes per pixel RGBA.
    rgba,
    /// 4 bytes per pixel BGRA.
    bgra,
};

// --- Sub-module re-exports: low-level D3D11/DXGI/COM bindings ---

pub const com = @import("directx11/com.zig");
pub const d3d11 = @import("directx11/d3d11.zig");
pub const dxgi = @import("directx11/dxgi.zig");

// --- Sub-module re-exports: renderer components from 025 ---

const devicepkg = @import("directx11/device.zig");
pub const Device = devicepkg.Device;
pub const CellPipeline = @import("directx11/cell_pipeline.zig").Pipeline;
pub const Constants = @import("directx11/cell_pipeline.zig").Constants;
pub const CellGrid = @import("directx11/cell_grid.zig").CellGrid;
pub const CellInstance = @import("directx11/cell_grid.zig").CellInstance;

// --- GraphicsAPI contract: mutable state ---

/// Runtime blending mode, set by GenericRenderer when config changes.
blending: configpkg.Config.AlphaBlending = .native,

/// The DX11 device managing the swap chain and render target.
device: ?Device = null,

/// Target for shared texture mode. Owns the texture and RTV.
/// Null for swap chain modes (Target borrows from Device).
shared_target: ?Target = null,

// --- GraphicsAPI contract: functions ---

pub fn init(alloc: Allocator, opts: rendererpkg.Options) !DirectX11 {
    _ = alloc;

    var result = DirectX11{};

    if (comptime builtin.os.tag != .windows) {
        return result;
    }

    switch (opts.rt_surface.platform) {
        .windows => |w| {
            const surface: devicepkg.Surface = if (w.hwnd) |hwnd|
                .{ .hwnd = hwnd }
            else if (w.swap_chain_panel) |panel|
                .{ .swap_chain_panel = @ptrCast(@alignCast(panel)) }
            else if (w.shared_texture_out) |out_ptr|
                .{ .shared_texture = .{
                    .handle_out = @ptrCast(@alignCast(out_ptr)),
                    .width = w.texture_width,
                    .height = w.texture_height,
                } }
            else
                @panic("Windows surface requires hwnd, swap_chain_panel, or shared_texture_out");

            const size = opts.size.screen;
            result.device = Device.init(surface, size.width, size.height) catch |err| {
                log.err("DX11 device init failed: {}", .{err});
                return error.DeviceInitFailed;
            };

            // For shared texture mode, create the Target now since it
            // owns the texture and RTV (instead of borrowing from Device).
            if (surface == .shared_texture) {
                const cfg = surface.shared_texture;
                result.shared_target = Target.initSharedTexture(
                    result.device.?.device,
                    cfg.width,
                    cfg.height,
                    cfg.handle_out,
                ) catch |err| {
                    log.err("shared texture init failed: {}", .{err});
                    result.device.?.deinit();
                    result.device = null;
                    return error.DeviceInitFailed;
                };
            }
        },
        else => @panic("unsupported platform for DX11"),
    }

    return result;
}

pub fn deinit(self: *DirectX11) void {
    if (self.shared_target) |*t| {
        t.deinit();
        self.shared_target = null;
    }
    if (self.device) |*dev| {
        dev.deinit();
    }
}

pub fn drawFrameStart(self: *DirectX11) void {
    _ = self;
    // No-op. Metal uses this for autorelease pools.
}

pub fn drawFrameEnd(self: *DirectX11) void {
    _ = self;
    // No-op.
}

pub fn initShaders(
    self: *const DirectX11,
    alloc: Allocator,
    custom_shaders: []const [:0]const u8,
) !shaders.Shaders {
    _ = alloc;
    _ = custom_shaders;
    const d3d_device = if (self.device) |dev| dev.device else null;
    return shaders.Shaders.init(d3d_device);
}

/// Notify the DX11 device of the desired surface dimensions.
/// For composition surfaces (no HWND), windowSize() will return these
/// values so the renderer's resize detection loop can pick them up.
pub fn setTargetSize(self: *DirectX11, width: u32, height: u32) void {
    if (self.device) |*dev| {
        dev.setTargetSize(width, height);
    }
}

pub fn surfaceSize(self: *const DirectX11) !struct { width: u32, height: u32 } {
    if (self.device) |dev| {
        // Query the actual window size, not the swap chain buffer size.
        // The swap chain buffer is only resized in beginFrame, so returning
        // dev.width/height here would create a circular dependency: drawFrame
        // compares surfaceSize() against self.size.screen to detect resize,
        // but dev.width/height only change after the resize is detected.
        const ws = dev.windowSize();
        return .{ .width = ws.width, .height = ws.height };
    }
    return .{ .width = 0, .height = 0 };
}

pub fn initTarget(self: *const DirectX11, width: usize, height: usize) !Target {
    // Shared texture mode: return a borrowed view of the shared target.
    // Only copy rtv and dimensions -- leave texture and handle_out null
    // so releaseOwnedResources treats this as borrowed (no double-release).
    if (self.shared_target) |st| {
        return .{
            .rtv = st.rtv,
            .width = st.width,
            .height = st.height,
        };
    }
    return .{
        .rtv = if (self.device) |dev| dev.rtv else null,
        .width = width,
        .height = height,
    };
}

pub inline fn beginFrame(
    self: *const DirectX11,
    renderer: *Renderer,
    target: *Target,
) !Frame {
    _ = self;

    // Resize the swap chain if the target dimensions don't match the
    // back buffer. initTarget can't do this because it receives the
    // API by value (const), but beginFrame has mutable access through
    // the renderer. device is ?Device (inline), so we need &renderer.api.
    if (renderer.api.device) |*dev| {
        const w: u32 = @intCast(target.width);
        const h: u32 = @intCast(target.height);
        if (dev.width != w or dev.height != h) {
            if (renderer.api.shared_target) |*st| {
                // Shared texture mode: Target owns the texture, resize it.
                st.resizeSharedTexture(dev.device, w, h) catch |err| {
                    log.err("shared texture resize failed: {}", .{err});
                    return error.PresentFailed;
                };
                // Update the borrowed view's rtv (texture stays null -- borrowed).
                target.rtv = st.rtv;
                dev.width = w;
                dev.height = h;
            } else {
                // Swap chain mode: Device owns the back buffer, resize it.
                dev.resize(w, h) catch |err| {
                    log.err("swap chain resize failed: {}", .{err});
                    return error.PresentFailed;
                };
                target.rtv = dev.rtv;
            }
        }
    }

    return try Frame.begin(.{}, renderer, target);
}

pub fn presentLastTarget(self: *DirectX11) !void {
    if (self.device) |*dev| {
        dev.present() catch |err| {
            log.err("present failed: {}", .{err});
            return error.PresentFailed;
        };
    }
}

pub inline fn bufferOptions(self: DirectX11) bufferpkg.Options {
    const dev = self.device orelse @panic("DX11 device not initialized");
    return .{
        .device = dev.device,
        .context = dev.context,
        .usage = .dynamic,
        .bind_flags = d3d11.D3D11_BIND_VERTEX_BUFFER,
    };
}

/// Instance buffers use the same options as vertex buffers -- both are
/// bound to the IA stage as vertex data via IASetVertexBuffers.
pub const instanceBufferOptions = bufferOptions;
pub const fgBufferOptions = bufferOptions;
pub const imageBufferOptions = bufferOptions;
pub const bgImageBufferOptions = bufferOptions;

/// Cell background buffer is bound as StructuredBuffer<uint> in HLSL,
/// not as a vertex buffer. It needs SHADER_RESOURCE binding and a
/// structure stride so the GPU can index into it by element.
pub inline fn bgBufferOptions(self: DirectX11) bufferpkg.Options {
    const dev = self.device orelse @panic("DX11 device not initialized");
    return .{
        .device = dev.device,
        .context = dev.context,
        .usage = .dynamic,
        .bind_flags = d3d11.D3D11_BIND_SHADER_RESOURCE,
        .structure_byte_stride = @sizeOf(u32),
    };
}

/// Uniform buffers are bound as constant buffers (HLSL cbuffer).
/// DX11 requires constant buffer sizes to be a multiple of 16 bytes.
pub inline fn uniformBufferOptions(self: DirectX11) bufferpkg.Options {
    const dev = self.device orelse @panic("DX11 device not initialized");
    return .{
        .device = dev.device,
        .context = dev.context,
        .usage = .dynamic,
        .bind_flags = d3d11.D3D11_BIND_CONSTANT_BUFFER,
    };
}

pub inline fn textureOptions(self: DirectX11) Texture.Options {
    const dev = self.device orelse @panic("DX11 device not initialized");
    return .{
        .device = dev.device,
        .context = dev.context,
        .format = .B8G8R8A8_UNORM,
    };
}

pub inline fn samplerOptions(self: DirectX11) Sampler.Options {
    const dev = self.device orelse @panic("DX11 device not initialized");
    return .{
        .device = dev.device,
    };
}

pub inline fn imageTextureOptions(
    self: DirectX11,
    format: ImageTextureFormat,
    srgb: bool,
) Texture.Options {
    _ = srgb; // DX11 sRGB handled via SRV format, not implemented yet
    const dev = self.device orelse @panic("DX11 device not initialized");
    const dxgi_format: dxgi.DXGI_FORMAT = switch (format) {
        .gray => .R8_UNORM,
        .rgba => .R8G8B8A8_UNORM,
        .bgra => .B8G8R8A8_UNORM,
    };
    return .{
        .device = dev.device,
        .context = dev.context,
        .format = dxgi_format,
    };
}

pub fn initAtlasTexture(
    self: *const DirectX11,
    atlas: *const font.Atlas,
) Texture.Error!Texture {
    const dev = self.device orelse @panic("DX11 device not initialized");

    // Map font atlas format to DXGI format.
    // Metal uses r8unorm / bgra8unorm_srgb; we use the DX11 equivalents.
    const dxgi_format: dxgi.DXGI_FORMAT = switch (atlas.format) {
        .grayscale => .R8_UNORM,
        .bgra => .B8G8R8A8_UNORM,
        else => std.debug.panic("unsupported atlas format for DX11 texture: {}", .{atlas.format}),
    };

    return try Texture.init(
        .{
            .device = dev.device,
            .context = dev.context,
            .format = dxgi_format,
        },
        @intCast(atlas.size),
        @intCast(atlas.size),
        atlas.data,
    );
}

test {
    _ = com;
    _ = d3d11;
    _ = dxgi;
}
