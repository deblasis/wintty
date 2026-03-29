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
    // Clear the render target using the first attachment's clear color.
    if (self.renderer.api.device) |*dev| {
        for (attachments) |att| {
            if (att.clear_color) |color| {
                dev.clearRenderTarget(.{
                    @floatCast(color[0]),
                    @floatCast(color[1]),
                    @floatCast(color[2]),
                    @floatCast(color[3]),
                });
                break;
            }
        }
    }
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
