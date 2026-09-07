//! The options that are used to configure a renderer.

const std = @import("std");
const apprt = @import("../apprt.zig");
const font = @import("../font/main.zig");
const renderer = @import("../renderer.zig");

/// The derived configuration for this renderer implementation.
config: renderer.Renderer.DerivedConfig,

/// The font grid that should be used along with the key for deref-ing.
font_grid: *font.SharedGrid,

/// The size of everything.
size: renderer.Size,

/// The mailbox for sending the surface messages. This is only valid
/// once the thread has started and should not be used outside of the thread.
surface_mailbox: apprt.surface.Mailbox,

/// The apprt surface.
rt_surface: *apprt.Surface,

/// The renderer thread.
thread: *renderer.Thread,

/// Wall-clock timestamp (`.awake` clock) captured at the top of
/// `Surface.init`, threaded through so GPU backend init phases (device
/// creation, PSO build, init-fence wait) can report elapsed time under
/// the `surface_init` scoped logger without their own plumbing. Optional
/// and defaults to null so this struct's other constructors, if any are
/// ever added, are not forced to supply it.
init_started: ?std.Io.Timestamp = null,
