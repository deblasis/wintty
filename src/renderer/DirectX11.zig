//! Graphics API wrapper for DirectX 11.
//!
//! This module provides the GraphicsAPI contract required by GenericRenderer,
//! mirroring the structure of Metal.zig and OpenGL.zig.
//!
//! Current status: stub - all functions panic at runtime. The contract is
//! satisfied at compile time so that GenericRenderer(DirectX11) compiles.
//! Infrastructure (COM bindings, device lifecycle, cell grid pipeline) is
//! already in place from prior work in the directx11/ subdirectory.
pub const DirectX11 = @This();

const std = @import("std");
const Allocator = std.mem.Allocator;

const configpkg = @import("../config.zig");
const font = @import("../font/main.zig");
const rendererpkg = @import("../renderer.zig");
const Renderer = rendererpkg.GenericRenderer(DirectX11);
const shadertoy = @import("shadertoy.zig");

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

pub const Device = @import("directx11/device.zig").Device;
pub const CellPipeline = @import("directx11/cell_pipeline.zig").Pipeline;
pub const Constants = @import("directx11/cell_pipeline.zig").Constants;
pub const CellGrid = @import("directx11/cell_grid.zig").CellGrid;
pub const CellInstance = @import("directx11/cell_grid.zig").CellInstance;

// --- GraphicsAPI contract: mutable state ---

/// Runtime blending mode, set by GenericRenderer when config changes.
blending: configpkg.Config.AlphaBlending = .native,

// --- GraphicsAPI contract: functions ---

pub fn init(alloc: Allocator, opts: rendererpkg.Options) !DirectX11 {
    _ = alloc;
    _ = opts;
    @panic("TODO: DX11 init");
}

pub fn deinit(self: *DirectX11) void {
    _ = self;
    @panic("TODO: DX11 deinit");
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
    _ = self;
    _ = alloc;
    _ = custom_shaders;
    @panic("TODO: DX11 initShaders");
}

pub fn surfaceSize(self: *const DirectX11) !struct { width: u32, height: u32 } {
    _ = self;
    @panic("TODO: DX11 surfaceSize");
}

pub fn initTarget(self: *const DirectX11, width: usize, height: usize) !Target {
    _ = self;
    _ = width;
    _ = height;
    @panic("TODO: DX11 initTarget");
}

pub inline fn beginFrame(
    self: *const DirectX11,
    renderer: *Renderer,
    target: *Target,
) !Frame {
    _ = self;
    return try Frame.begin(.{}, renderer, target);
}

pub fn presentLastTarget(self: *DirectX11) !void {
    _ = self;
    @panic("TODO: DX11 presentLastTarget");
}

pub inline fn bufferOptions(self: DirectX11) bufferpkg.Options {
    _ = self;
    return .{};
}

pub const instanceBufferOptions = bufferOptions;
pub const uniformBufferOptions = bufferOptions;
pub const fgBufferOptions = bufferOptions;
pub const bgBufferOptions = bufferOptions;
pub const imageBufferOptions = bufferOptions;
pub const bgImageBufferOptions = bufferOptions;

pub inline fn textureOptions(self: DirectX11) Texture.Options {
    _ = self;
    return .{};
}

pub inline fn samplerOptions(self: DirectX11) Sampler.Options {
    _ = self;
    return .{};
}

pub inline fn imageTextureOptions(
    self: DirectX11,
    format: ImageTextureFormat,
    srgb: bool,
) Texture.Options {
    _ = self;
    _ = format;
    _ = srgb;
    return .{};
}

pub fn initAtlasTexture(
    self: *const DirectX11,
    atlas: *const font.Atlas,
) Texture.Error!Texture {
    _ = self;
    _ = atlas;
    @panic("TODO: DX11 initAtlasTexture");
}

test {
    _ = com;
    _ = d3d11;
    _ = dxgi;
}
