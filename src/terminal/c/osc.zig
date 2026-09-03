const std = @import("std");
const lib = @import("../lib.zig");
const CAllocator = lib.alloc.Allocator;
const osc = @import("../osc.zig");
const Result = @import("result.zig").Result;

const log = std.log.scoped(.osc);

/// C: GhosttyOscParser
pub const Parser = ?*osc.Parser;

/// C: GhosttyOscCommand
pub const Command = ?*osc.Command;

/// C: GhosttyOscCommandType
pub const CommandType = osc.Command.Key;

pub fn new(
    alloc_: ?*const CAllocator,
    result: *Parser,
) callconv(lib.calling_conv) Result {
    const alloc = lib.alloc.default(alloc_);
    const ptr = alloc.create(osc.Parser) catch
        return .out_of_memory;
    ptr.* = .init(alloc);
    result.* = ptr;
    return .success;
}

pub fn free(parser_: Parser) callconv(lib.calling_conv) void {
    // C-built parsers always have an associated allocator.
    const parser = parser_ orelse return;
    const alloc = parser.alloc.?;
    parser.deinit();
    alloc.destroy(parser);
}

pub fn reset(parser_: Parser) callconv(lib.calling_conv) void {
    parser_.?.reset();
}

pub fn next(parser_: Parser, byte: u8) callconv(lib.calling_conv) void {
    parser_.?.next(byte);
}

pub fn end(parser_: Parser, terminator: u8) callconv(lib.calling_conv) Command {
    return parser_.?.end(terminator);
}

pub fn commandType(command_: Command) callconv(lib.calling_conv) CommandType {
    const command = command_ orelse return .invalid;
    return command.*;
}

/// C: GhosttyOscCommandData
pub const CommandData = enum(c_int) {
    invalid = 0,
    change_window_title_str = 1,

    // OSC 7777 prompt report. Each field is queried on its own, and a query
    // for a field the report did not carry returns false: absent is a real
    // answer here, distinct from an empty value.
    //
    // There is no query for the schema version: the parser rejects every
    // version but the one it was built for, so an accessor for it could only
    // ever answer 1.
    prompt_report_cwd_str = 2,
    prompt_report_exit_code_i64 = 3,
    prompt_report_shell_str = 4,
    prompt_report_git_head_str = 5,
    prompt_report_git_branch_str = 6,
    prompt_report_git_dirty_bool = 7,

    /// Output type expected for querying the data of the given kind.
    pub fn OutType(comptime self: CommandData) type {
        return switch (self) {
            .invalid => void,
            .change_window_title_str,
            .prompt_report_cwd_str,
            .prompt_report_shell_str,
            .prompt_report_git_head_str,
            .prompt_report_git_branch_str,
            => [*:0]const u8,
            .prompt_report_exit_code_i64 => i64,
            .prompt_report_git_dirty_bool => bool,
        };
    }
};

pub fn commandData(
    command_: Command,
    data: CommandData,
    out: ?*anyopaque,
) callconv(lib.calling_conv) bool {
    if (comptime std.debug.runtime_safety) {
        _ = std.enums.fromInt(CommandData, @intFromEnum(data)) orelse {
            log.warn("commandData invalid data value={d}", .{@intFromEnum(data)});
            return false;
        };
    }

    return switch (data) {
        .invalid => false,
        inline else => |comptime_data| commandDataTyped(
            command_,
            comptime_data,
            @ptrCast(@alignCast(out)),
        ),
    };
}

fn commandDataTyped(
    command_: Command,
    comptime data: CommandData,
    out: *data.OutType(),
) bool {
    const command = command_.?;
    switch (data) {
        .invalid => return false,
        .change_window_title_str => switch (command.*) {
            .change_window_title => |v| out.* = v.ptr,
            else => return false,
        },

        .prompt_report_cwd_str => switch (command.*) {
            .prompt_report => |v| out.* = v.cwd.ptr,
            else => return false,
        },

        .prompt_report_exit_code_i64 => switch (command.*) {
            .prompt_report => |v| out.* = v.exit_code orelse return false,
            else => return false,
        },

        .prompt_report_shell_str => switch (command.*) {
            .prompt_report => |v| out.* = (v.shell orelse return false).ptr,
            else => return false,
        },

        .prompt_report_git_head_str => switch (command.*) {
            .prompt_report => |v| out.* = (v.git_head orelse return false).ptr,
            else => return false,
        },

        .prompt_report_git_branch_str => switch (command.*) {
            .prompt_report => |v| out.* = (v.git_branch orelse return false).ptr,
            else => return false,
        },

        .prompt_report_git_dirty_bool => switch (command.*) {
            .prompt_report => |v| out.* = v.git_dirty orelse return false,
            else => return false,
        },
    }

    return true;
}

test "alloc" {
    const testing = std.testing;
    var p: Parser = undefined;
    try testing.expectEqual(Result.success, new(
        &lib.alloc.test_allocator,
        &p,
    ));
    free(p);
}

test "command type null" {
    const testing = std.testing;
    try testing.expectEqual(.invalid, commandType(null));
}

test "change window title" {
    const testing = std.testing;
    var p: Parser = undefined;
    try testing.expectEqual(Result.success, new(
        &lib.alloc.test_allocator,
        &p,
    ));
    defer free(p);

    // Parse it
    next(p, '0');
    next(p, ';');
    next(p, 'a');
    const cmd = end(p, 0);
    try testing.expectEqual(.change_window_title, commandType(cmd));

    // Extract the title
    var title: [*:0]const u8 = undefined;
    try testing.expect(commandData(cmd, .change_window_title_str, @ptrCast(&title)));
    try testing.expectEqualStrings("a", std.mem.span(title));
}

test "prompt report" {
    const testing = std.testing;
    var p: Parser = undefined;
    try testing.expectEqual(Result.success, new(
        &lib.alloc.test_allocator,
        &p,
    ));
    defer free(p);

    // {"v":1,"cwd":"C:\\x","exit":7}
    const input = "7777;p;7B2276223A312C22637764223A22433A5C5C78222C2265786974223A377D";
    for (input) |ch| next(p, ch);
    const cmd = end(p, 0x07);
    try testing.expectEqual(.prompt_report, commandType(cmd));

    var cwd: [*:0]const u8 = undefined;
    try testing.expect(commandData(cmd, .prompt_report_cwd_str, @ptrCast(&cwd)));
    try testing.expectEqualStrings("C:\\x", std.mem.span(cwd));

    var exit_code: i64 = 0;
    try testing.expect(commandData(cmd, .prompt_report_exit_code_i64, @ptrCast(&exit_code)));
    try testing.expectEqual(@as(i64, 7), exit_code);

    // Absent is answered as absent, not as a zero value.
    var dirty: bool = true;
    try testing.expect(!commandData(cmd, .prompt_report_git_dirty_bool, @ptrCast(&dirty)));

    var shell: [*:0]const u8 = undefined;
    try testing.expect(!commandData(cmd, .prompt_report_shell_str, @ptrCast(&shell)));
}
