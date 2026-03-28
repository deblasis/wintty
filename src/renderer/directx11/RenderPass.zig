//! Render pass for DX11 draw steps.
//! TODO: Implement with ID3D11DeviceContext draw calls.
const Pipeline = @import("Pipeline.zig");
const Sampler = @import("Sampler.zig");
const Target = @import("Target.zig");
const Texture = @import("Texture.zig");
const bufferpkg = @import("buffer.zig");

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
    pipeline: Pipeline,
    uniforms: ?bufferpkg.Buffer(u8) = null,
    buffers: []const ?bufferpkg.Buffer(u8) = &.{},
    textures: []const ?Texture = &.{},
    samplers: []const ?Sampler = &.{},
    draw: Draw,

    pub const Draw = struct {
        type: DrawType,
        vertex_count: usize,
        instance_count: usize = 1,
    };

    pub const DrawType = enum {
        triangle,
        triangle_strip,
    };
};

pub fn begin(opts: Options) @This() {
    _ = opts;
    @panic("TODO: DX11 RenderPass.begin");
}

pub fn step(self: *@This(), s: Step) void {
    _ = self;
    _ = s;
    @panic("TODO: DX11 RenderPass.step");
}

pub fn complete(self: *const @This()) void {
    _ = self;
    @panic("TODO: DX11 RenderPass.complete");
}
