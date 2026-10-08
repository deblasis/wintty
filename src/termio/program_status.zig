//! Whether this process acts on the program status protocol (OSC 7501).
//!
//! The protocol only works if something keeps the records a program
//! reports, and the core keeps none: the embedder does. So the core
//! answers the support query (`OSC 7501 ; ?`) and forwards reports only
//! once the embedder said it consumes them, through
//! `ghostty_app_set_program_status`. Until then a program sees no reply
//! and knows not to send reports, and any it sends anyway are ignored.
//!
//! This is process-wide rather than per surface because it describes the
//! embedder, of which a process has one. Every surface reads it on each
//! event, so turning it on takes effect for surfaces that already exist.

const std = @import("std");

/// Off by default. Read by every stream handler; written by the embedder.
pub var enabled: std.atomic.Value(bool) = .init(false);

pub fn set(value: bool) void {
    enabled.store(value, .release);
}
