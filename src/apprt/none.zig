const std = @import("std");
const Allocator = std.mem.Allocator;

const internal_os = @import("../os/main.zig");
const apprt = @import("../apprt.zig");
pub const resourcesDir = internal_os.resourcesDir;

pub const App = struct {
    /// No-op: there is no app loop under this runtime, so there is nothing
    /// to wake. It exists because `App.Mailbox.push` wakes the app after
    /// every surface message, and without it any code path that notifies a
    /// surface fails to compile here rather than at the point it would
    /// actually need a loop. That took `src/termio` out of reach of
    /// `-Dapp-runtime=none`, which is the only configuration that compiles
    /// it at all.
    pub fn wakeup(_: *const App) void {}

    /// Always return false: there is no apprt here to hand an action to,
    /// so nothing is ever performed. It exists for the same reason
    /// `wakeup` does. Without it any core code path that notifies the
    /// apprt fails to compile under this runtime, and since this is the
    /// runtime the unit test binary is built with, no test could reach
    /// one -- `Surface.endKeySequence` among them.
    pub fn performAction(
        _: *App,
        _: apprt.Target,
        comptime action: apprt.Action.Key,
        _: apprt.Action.Value(action),
    ) !bool {
        return false;
    }

    /// Always return false as there is no apprt to communicate with.
    pub fn performIpc(
        _: Allocator,
        _: apprt.ipc.Target,
        comptime action: apprt.ipc.Action.Key,
        _: apprt.ipc.Action.Value(action),
    ) !bool {
        return false;
    }
};
pub const Surface = struct {};
