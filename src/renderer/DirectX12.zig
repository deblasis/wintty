//! Graphics API wrapper for DirectX 12.
//!
//! This module provides the GraphicsAPI contract required by GenericRenderer,
//! mirroring the structure of Metal.zig, OpenGL.zig, and the previous
//! DirectX11.zig. All functions are stubs that will be replaced as the
//! DX12 backend is built out in subsequent PRs.
pub const DirectX12 = @This();

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

// --- Sub-module re-exports: low-level D3D12/DXGI/COM bindings ---

pub const com = @import("directx12/com.zig");
pub const d3d12 = @import("directx12/d3d12.zig");
pub const dcomp = @import("directx12/dcomp.zig");
pub const descriptor_heap = @import("directx12/descriptor_heap.zig");
pub const device = @import("directx12/device.zig");
pub const dxgi = @import("directx12/dxgi.zig");

// --- GraphicsAPI contract: mutable state ---

/// Runtime blending mode, set by GenericRenderer when config changes.
blending: configpkg.Config.AlphaBlending = .native,

// --- GraphicsAPI contract: functions ---

pub fn init(alloc: Allocator, opts: rendererpkg.Options) !DirectX12 {
    _ = alloc;
    _ = opts;
    log.warn("DX12 backend is a stub -- no GPU output yet", .{});
    return .{};
}

pub fn deinit(self: *DirectX12) void {
    _ = self;
}

pub fn drawFrameStart(self: *DirectX12) void {
    _ = self;
}

pub fn drawFrameEnd(self: *DirectX12) void {
    _ = self;
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
}

pub fn surfaceSize(self: *const DirectX12) !struct { width: u32, height: u32 } {
    _ = self;
    return .{ .width = 0, .height = 0 };
}

pub fn initTarget(self: *const DirectX12, width: usize, height: usize) !Target {
    _ = self;
    return .{ .width = width, .height = height };
}

pub inline fn beginFrame(
    self: *const DirectX12,
    renderer: *Renderer,
    target: *Target,
) !Frame {
    _ = self;
    return .{
        .renderer = renderer,
        .target = target,
    };
}

pub fn presentLastTarget(self: *DirectX12) !void {
    _ = self;
}

pub inline fn bufferOptions(self: DirectX12) bufferpkg.Options {
    _ = self;
    return .{};
}

pub const instanceBufferOptions = bufferOptions;
pub const fgBufferOptions = bufferOptions;
pub const imageBufferOptions = bufferOptions;
pub const bgImageBufferOptions = bufferOptions;

pub inline fn bgBufferOptions(self: DirectX12) bufferpkg.Options {
    _ = self;
    return .{};
}

pub inline fn uniformBufferOptions(self: DirectX12) bufferpkg.Options {
    _ = self;
    return .{};
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
