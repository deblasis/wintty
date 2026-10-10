//! Helpers for parsing metadata shared by Kitty OSC protocols.
//!
//! Kitty OSC 99 and OSC 5522 encode metadata as colon-separated `key=value`
//! fields. The iterator in this module lazily searches that metadata for one
//! key, preserving the order of repeated values without allocating.
//!
//! Parsing is intentionally tolerant. Whitespace around keys and values is
//! trimmed, while malformed fields, non-matching keys, and invalid values are
//! skipped. Returned values are slices of the original metadata and remain
//! valid only as long as that input remains valid.

const std = @import("std");

/// Return an iterator over values whose key exactly matches `key`.
///
/// If `valid_value_characters` is non-null, every byte in a returned value must
/// appear in that character set. Passing null disables value validation. Both
/// arguments are comptime-known so each protocol can specialize the iterator
/// for its metadata grammar without storing a key or validator at runtime.
pub fn ValueIterator(
    comptime key: []const u8,
    comptime valid_value_characters: ?[]const u8,
) type {
    return struct {
        const Self = @This();

        metadata: []const u8,
        pos: usize,

        /// Initialize an iterator borrowing `metadata`.
        pub fn init(metadata: []const u8) Self {
            return .{
                .metadata = metadata,
                .pos = 0,
            };
        }

        /// Return the next valid matching value, or null when none remain.
        /// The returned slice borrows the metadata passed to `init`.
        pub fn next(self: *Self) ?[]const u8 {
            while (self.pos < self.metadata.len) {
                const end = std.mem.indexOfScalarPos(
                    u8,
                    self.metadata,
                    self.pos,
                    ':',
                ) orelse self.metadata.len;
                const field = self.metadata[self.pos..end];
                self.pos = if (end < self.metadata.len) end + 1 else end;

                const equals = std.mem.indexOfScalar(u8, field, '=') orelse
                    continue;
                const field_key = std.mem.trim(
                    u8,
                    field[0..equals],
                    &std.ascii.whitespace,
                );
                if (!std.mem.eql(u8, field_key, key)) continue;

                const value = std.mem.trim(
                    u8,
                    field[equals + 1 ..],
                    &std.ascii.whitespace,
                );
                if (valid_value_characters) |valid| {
                    if (std.mem.indexOfNone(u8, value, valid) != null) continue;
                }

                return value;
            }

            return null;
        }
    };
}

test "ValueIterator skips malformed and prefix-matching keys" {
    const testing = std.testing;
    var it: ValueIterator("id", null) = .init(
        "id-extra=wrong:id: id = first :id=second",
    );

    try testing.expectEqualStrings("first", it.next().?);
    try testing.expectEqualStrings("second", it.next().?);
    try testing.expect(it.next() == null);
}

/// Variant of [`ValueIterator`] for protocols whose contract says a
/// matching-but-invalid value must poison the whole parse instead of
/// being skipped: `next` returns the next valid value, null at the end,
/// and `error.InvalidValue` on the first matching value that contains a
/// byte outside `valid_value_characters`.
///
/// The plain iterator is right for the Kitty protocols this module was
/// written for, whose documents say to skip bad values. OSC 7501 is the
/// opposite: its parser promises that an invalid id discards the report
/// so a report meant for one record can never fall back to the root
/// record -- and a skipped id reads as "no id given", which is exactly
/// that fallback.
pub fn StrictValueIterator(
    comptime key: []const u8,
    comptime valid_value_characters: []const u8,
) type {
    return struct {
        const Self = @This();

        metadata: []const u8,
        pos: usize,

        /// Initialize an iterator borrowing `metadata`.
        pub fn init(metadata: []const u8) Self {
            return .{
                .metadata = metadata,
                .pos = 0,
            };
        }

        /// Return the next matching value, or null when none remain.
        /// A matching value with a byte outside the alphabet is
        /// `error.InvalidValue`, not a skip.
        pub fn next(self: *Self) error{InvalidValue}!?[]const u8 {
            while (self.pos < self.metadata.len) {
                const end = std.mem.indexOfScalarPos(
                    u8,
                    self.metadata,
                    self.pos,
                    ':',
                ) orelse self.metadata.len;
                const field = self.metadata[self.pos..end];
                self.pos = if (end < self.metadata.len) end + 1 else end;

                const equals = std.mem.indexOfScalar(u8, field, '=') orelse
                    continue;
                const field_key = std.mem.trim(
                    u8,
                    field[0..equals],
                    &std.ascii.whitespace,
                );
                if (!std.mem.eql(u8, field_key, key)) continue;

                const value = std.mem.trim(
                    u8,
                    field[equals + 1 ..],
                    &std.ascii.whitespace,
                );
                if (std.mem.indexOfNone(u8, value, valid_value_characters) != null)
                    return error.InvalidValue;

                return value;
            }

            return null;
        }
    };
}

test "StrictValueIterator poisons on disallowed characters" {
    const testing = std.testing;

    var poisoned: StrictValueIterator("id", "abc") = .init("id=a!b:id=abc");
    try testing.expectError(error.InvalidValue, poisoned.next());

    var clean: StrictValueIterator("id", "abc") = .init("other=x:id=abc");
    try testing.expectEqualStrings("abc", (try clean.next()).?);
    try testing.expect((try clean.next()) == null);
}

test "ValueIterator skips values containing disallowed characters" {
    const testing = std.testing;
    var it: ValueIterator("i", "abc") = .init("i=a?:i=abc");

    try testing.expectEqualStrings("abc", it.next().?);
    try testing.expect(it.next() == null);
}
