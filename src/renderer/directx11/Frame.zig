//! Frame context for DX11 draw commands.
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
    self: *@This(),
    attachments: []const RenderPass.Options.Attachment,
) RenderPass {
    if (self.renderer.api.device) |*dev| {
        return RenderPass.begin(dev.context, dev.device, .{ .attachments = attachments });
    } else {
        return RenderPass.begin(null, null, .{ .attachments = attachments });
    }
}

pub fn complete(self: *@This(), sync: bool) void {
    // DX11 immediate mode: commands already executed. Present happens in
    // presentLastTarget(), called by GenericRenderer after frame completion.
    _ = self;
    _ = sync;
}
