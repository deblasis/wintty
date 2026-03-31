//! Frame context for DX11 draw commands.
const std = @import("std");
const log = std.log.scoped(.directx11);

const DirectX11 = @import("../DirectX11.zig");
const Renderer = @import("../generic.zig").Renderer(DirectX11);
const Target = @import("Target.zig");
const RenderPass = @import("RenderPass.zig");
const Health = @import("../../renderer.zig").Health;

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
        // Composition surfaces use premultiplied alpha, so the clear
        // color must be opaque to prevent the host background from
        // showing through uncovered pixels. HWND surfaces use
        // UNSPECIFIED alpha mode where alpha is ignored anyway.
        if (dev.hwnd == null) {
            var patched: [8]RenderPass.Options.Attachment = undefined;
            const n = @min(attachments.len, patched.len);
            for (attachments[0..n], patched[0..n]) |src, *dst| {
                dst.* = src;
                if (dst.clear_color) |*c| c[3] = 1.0;
            }
            return RenderPass.begin(dev.context, dev.device, dev.blend_state, .{ .attachments = patched[0..n] });
        }
        return RenderPass.begin(dev.context, dev.device, dev.blend_state, .{ .attachments = attachments });
    } else {
        return RenderPass.begin(null, null, null, .{ .attachments = attachments });
    }
}

pub fn complete(self: *@This(), sync: bool) void {
    _ = sync;
    // DX11 immediate mode: draw commands already executed on the device
    // context. Present the swap chain to show the frame.
    var health: Health = .healthy;
    if (self.renderer.api.device) |*dev| {
        dev.present() catch |err| {
            log.err("present failed: {}", .{err});
            health = .unhealthy;
        };
    }

    // Release the frame back to the swap chain so the next drawFrame
    // can acquire it. Without this, the semaphore in SwapChain.nextFrame
    // runs out of permits after buf_count frames and blocks forever.
    self.renderer.frameCompleted(health);
}
