//! DX12 frame context stub.
//!
//! Will be replaced with a real implementation managing command allocators,
//! command lists, and fence synchronization for each in-flight frame.
const DirectX12 = @import("../DirectX12.zig");
const Renderer = @import("../generic.zig").Renderer(DirectX12);
const Target = @import("Target.zig");
const RenderPass = @import("RenderPass.zig");
const Health = @import("../../renderer.zig").Health;

renderer: *Renderer,
target: *Target,

pub fn renderPass(
    self: *@This(),
    attachments: []const RenderPass.Options.Attachment,
) RenderPass {
    _ = self;
    _ = attachments;
    return .{};
}

pub fn complete(self: *@This(), sync: bool) void {
    _ = sync;
    self.renderer.frameCompleted(.healthy);
}
