//! Shader management and GPU data structs for DX12.
//!
//! Re-exports the shared GPU data structs from gpu_data.zig and provides
//! a stub Shaders type that satisfies the GenericRenderer contract.
const std = @import("std");

const d3d12 = @import("d3d12.zig");
const gpu_data = @import("gpu_data.zig");
const Pipeline = @import("Pipeline.zig");

pub const Uniforms = gpu_data.Uniforms;
pub const CellText = gpu_data.CellText;
pub const CellBg = gpu_data.CellBg;
pub const Image = gpu_data.Image;
pub const BgImage = gpu_data.BgImage;

/// Shader management for DX12.
pub const Shaders = struct {
    /// Shared root signature owned by this Shaders instance.
    /// All pipelines reference it but only Shaders releases it.
    root_signature: ?*d3d12.ID3D12RootSignature = null,
    pipelines: Pipelines = .{},
    post_pipelines: []const Pipeline = &.{},
    defunct: bool = false,

    pub const Pipelines = struct {
        bg_color: Pipeline = .{},
        cell_bg: Pipeline = .{},
        cell_text: Pipeline = .{},
        image: Pipeline = .{},
        bg_image: Pipeline = .{},
    };

    pub fn deinit(self: *Shaders, alloc: std.mem.Allocator) void {
        for (self.post_pipelines) |p| {
            p.deinit();
        }
        if (self.post_pipelines.len > 0) {
            alloc.free(self.post_pipelines);
        }
        self.post_pipelines = &.{};

        self.pipelines.bg_color.deinit();
        self.pipelines.cell_bg.deinit();
        self.pipelines.cell_text.deinit();
        self.pipelines.image.deinit();
        self.pipelines.bg_image.deinit();
        self.pipelines = .{};

        if (self.root_signature) |rs| _ = rs.Release();
        self.root_signature = null;
    }
};
