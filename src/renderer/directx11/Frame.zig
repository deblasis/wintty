//! Frame context for DX11 draw commands.
//! TODO: Implement with ID3D11DeviceContext deferred or immediate context.
const DirectX11 = @import("../DirectX11.zig");
const Renderer = @import("../generic.zig").Renderer(DirectX11);
const Target = @import("Target.zig");
const RenderPass = @import("RenderPass.zig");

/// Options for beginning a frame.
pub const Options = struct {};

renderer: *Renderer,
target: *Target,

pub fn begin(
    opts: Options,
    renderer: *Renderer,
    target: *Target,
) !@This() {
    _ = opts;
    return .{
        .renderer = renderer,
        .target = target,
    };
}

pub inline fn renderPass(
    self: *const @This(),
    attachments: []const RenderPass.Options.Attachment,
) RenderPass {
    _ = self;
    _ = attachments;
    @panic("TODO: DX11 Frame.renderPass");
}

pub fn complete(self: *@This(), sync: bool) void {
    _ = self;
    _ = sync;
    @panic("TODO: DX11 Frame.complete");
}
