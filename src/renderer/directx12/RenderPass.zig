//! DX12 render pass stub.
//!
//! Will be replaced with a real implementation that records draw commands
//! into an ID3D12GraphicsCommandList.
const Pipeline = @import("Pipeline.zig");
const Sampler = @import("Sampler.zig");
const Target = @import("Target.zig");
const Texture = @import("Texture.zig");
const bufferpkg = @import("buffer.zig");
const RawBuffer = bufferpkg.RawBuffer;

/// Options for beginning a render pass.
pub const Options = struct {
    attachments: []const Attachment,

    pub const Attachment = struct {
        target: union(enum) {
            texture: Texture,
            target: Target,
        },
        clear_color: ?[4]f64 = null,
    };
};

/// A single step in a render pass.
pub const Step = struct {
    pipeline: Pipeline = .{},
    uniforms: ?RawBuffer = null,
    buffers: []const ?RawBuffer = &.{},
    textures: []const ?Texture = &.{},
    samplers: []const ?Sampler = &.{},
    draw: Draw = .{},

    pub const Draw = struct {
        type: DrawType = .triangle,
        vertex_count: usize = 0,
        instance_count: usize = 1,
    };

    pub const DrawType = enum {
        triangle,
        triangle_strip,
    };
};

pub fn begin(opts: Options) @This() {
    _ = opts;
    return .{};
}

pub fn step(self: *@This(), s: Step) void {
    _ = self;
    _ = s;
}

pub fn complete(self: *const @This()) void {
    _ = self;
}
