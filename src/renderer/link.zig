const std = @import("std");
const assert = std.debug.assert;
const Allocator = std.mem.Allocator;
const oni = @import("oniguruma");
const inputpkg = @import("../input.zig");
const terminal = @import("../terminal/main.zig");
const point = terminal.point;
const Screen = terminal.Screen;
const Terminal = terminal.Terminal;

const log = std.log.scoped(.renderer_link);

/// How many regex retry budget overruns a single link scan tolerates
/// before it stops scanning.
///
/// Oniguruma's retry limit is per search call, so a scan that restarts a
/// search at every start position also restarts the budget, and the total
/// work becomes the budget times the viewport size. Counting overruns for
/// the whole scan puts a ceiling on that total while still letting the
/// scan step over a few expensive positions.
const scan_overrun_budget: usize = 3;

/// The link configuration needed for renderers.
pub const Link = struct {
    /// The regular expression to match the link against.
    regex: oni.Regex,

    /// The situations in which the link should be highlighted.
    highlight: inputpkg.Link.Highlight,

    pub fn deinit(self: *Link) void {
        self.regex.deinit();
    }

    /// Returns true if this link's highlight condition matches the given mouse state.
    fn active(
        self: *const Link,
        mouse_viewport: ?point.Coordinate,
        mouse_mods: inputpkg.Mods,
    ) bool {
        return switch (self.highlight) {
            .always => true,
            .always_mods => |v| mouse_mods.equal(v),
            .hover => mouse_viewport != null,
            .hover_mods => |v| mouse_viewport != null and mouse_mods.equal(v),
        };
    }
};

/// A set of links. This provides a higher level API for renderers
/// to match against a viewport and determine if cells are part of
/// a link.
pub const Set = struct {
    links: []Link,

    /// Returns the slice of links from the configuration.
    pub fn fromConfig(
        alloc: Allocator,
        config: []const inputpkg.Link,
    ) !Set {
        var links: std.ArrayList(Link) = .empty;
        defer links.deinit(alloc);

        for (config) |link| {
            var regex = try link.oniRegex();
            errdefer regex.deinit();
            try links.append(alloc, .{
                .regex = regex,
                .highlight = link.highlight,
            });
        }

        return .{ .links = try links.toOwnedSlice(alloc) };
    }

    pub fn deinit(self: *Set, alloc: Allocator) void {
        for (self.links) |*link| link.deinit();
        alloc.free(self.links);
    }

    /// Fills matches with the matches from regex link matches.
    pub fn renderCellMap(
        self: *const Set,
        alloc: Allocator,
        result: *terminal.RenderState.CellSet,
        render_state: *const terminal.RenderState,
        mouse_viewport: ?point.Coordinate,
        mouse_mods: inputpkg.Mods,
    ) !void {
        // Fast path, not very likely since we have default links.
        if (self.links.len == 0) return;

        // Determine if any links are active before building the string and
        // byte-to-cell map. Those buffers scale with viewport size and this
        // function runs during frame updates, so avoid allocating them when
        // the current mouse/modifier state can't highlight any regex links.
        for (self.links) |*link| {
            if (link.active(mouse_viewport, mouse_mods)) break;
        } else return;

        // Convert our render state to a string + byte map.
        var builder: std.Io.Writer.Allocating = .init(alloc);
        defer builder.deinit();
        var map: terminal.RenderState.StringMap = .empty;
        defer map.deinit(alloc);
        try render_state.string(&builder.writer, .{
            .alloc = alloc,
            .map = &map,
        });

        const str = builder.writer.buffered();

        // Bound the backtracking work per search. This runs on every frame
        // update and a link regex can backtrack catastrophically on some
        // viewport contents, so use the same budget as the click path.
        var match_param = try oni.MatchParam.init();
        defer match_param.deinit();
        try match_param.setRetryLimitInSearch(
            terminal.StringMap.oni_search_retry_limit,
        );

        // Overruns are counted for the whole scan so that the budget above
        // bounds the total work, not the work at one start position.
        var overruns_left: usize = scan_overrun_budget;

        // Go through each link and see if we have any matches.
        for (self.links) |*link| {
            if (!link.active(mouse_viewport, mouse_mods)) continue;

            var offset: usize = 0;
            while (offset < str.len) {
                var region = link.regex.searchWithParam(
                    str[offset..],
                    .{},
                    &match_param,
                ) catch |err| switch (err) {
                    error.Mismatch => break,

                    // We ran out of budget somewhere in the rest of the
                    // viewport, and Oniguruma doesn't tell us which start
                    // position was expensive. Skip the run of text we are
                    // sitting on and keep scanning, so one pathological
                    // position doesn't hide every link after it, but stop
                    // once the scan has spent its overrun budget.
                    error.RetryLimitInMatchOver,
                    error.RetryLimitInSearchOver,
                    error.MatchStackLimitOver,
                    error.SubexpCallLimitInSearchOver,
                    => {
                        if (overruns_left == 0) break;
                        overruns_left -= 1;
                        offset = skipRun(str, offset);
                        continue;
                    },

                    else => return err,
                };
                defer region.deinit();

                // We have a match!
                const offset_start: usize = @intCast(region.starts()[0]);
                const offset_end: usize = @intCast(region.ends()[0]);
                const start = offset + offset_start;
                const end = offset + offset_end;

                // Increment our offset by the number of bytes in the match.
                // We defer this so that we can return the match before
                // modifying the offset.
                defer offset = end;

                switch (link.highlight) {
                    .always, .always_mods => {},
                    .hover, .hover_mods => if (mouse_viewport) |vp| {
                        for (map.items[start..end]) |pt| {
                            if (pt.eql(vp)) break;
                        } else continue;
                    } else continue,
                }

                // Record the match
                for (map.items[start..end]) |pt| {
                    try result.put(alloc, pt, {});
                }
            }
        }
    }

    /// The offset just past the whitespace-delimited run of text at
    /// `offset`, used to step over a search that blew its retry budget.
    ///
    /// A single codepoint would be enough to make progress, but the next
    /// search then re-reads the same expensive text and spends a fresh
    /// budget on it, once per byte, which is how a per-search budget
    /// turns back into unbounded work. A whitespace-delimited run is the
    /// smallest unit that gets us past the candidate we could not match,
    /// and a search that overran found no match before it, so nothing
    /// that would have been highlighted is lost except a match starting
    /// inside that run. The default path patterns can match a single
    /// space, so such a match is possible; giving it up on pathological
    /// input is the price of the budget.
    ///
    /// Always advances by at least one byte when `offset < str.len`.
    fn skipRun(str: []const u8, offset: usize) usize {
        const whitespace = " \t\r\n";
        var i: usize = offset;
        while (i < str.len and
            std.mem.indexOfScalar(u8, whitespace, str[i]) != null) i += 1;
        while (i < str.len and
            std.mem.indexOfScalar(u8, whitespace, str[i]) == null) i += 1;
        return i;
    }
};

test "renderCellMap" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var t: terminal.Terminal = try .init(testing.io, alloc, .{
        .cols = 5,
        .rows = 3,
    });
    defer t.deinit(alloc);

    var s = t.vtStream();
    defer s.deinit();
    const str = "1ABCD2EFGH\r\n3IJKL";
    s.nextSlice(str);

    var state: terminal.RenderState = .empty;
    defer state.deinit(alloc);
    try state.update(alloc, &t);

    // Get a set
    var set = try Set.fromConfig(alloc, &.{
        .{
            .regex = "AB",
            .action = .{ .open = {} },
            .highlight = .{ .always = {} },
        },

        .{
            .regex = "EF",
            .action = .{ .open = {} },
            .highlight = .{ .always = {} },
        },
    });
    defer set.deinit(alloc);

    // Get our matches
    var result: terminal.RenderState.CellSet = .empty;
    defer result.deinit(alloc);
    try set.renderCellMap(
        alloc,
        &result,
        &state,
        null,
        .{},
    );
    try testing.expect(!result.contains(.{ .x = 0, .y = 0 }));
    try testing.expect(result.contains(.{ .x = 1, .y = 0 }));
    try testing.expect(result.contains(.{ .x = 2, .y = 0 }));
    try testing.expect(!result.contains(.{ .x = 3, .y = 0 }));
    try testing.expect(result.contains(.{ .x = 1, .y = 1 }));
    try testing.expect(!result.contains(.{ .x = 1, .y = 2 }));
}

test "renderCellMap bounds regex backtracking" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // A URL followed by a long run of trailing punctuation. The default
    // URL regex has to consider every way of splitting that run between
    // its repeated groups, which is exponential work, so this only
    // finishes because the search has a retry budget.
    const pathological = "https://x.com/" ++ ("." ** 40);
    const trailing_url = "https://b.com";
    const row = pathological ++ " " ++ trailing_url;

    var t: terminal.Terminal = try .init(testing.io, alloc, .{
        .cols = row.len,
        .rows = 2,
    });
    defer t.deinit(alloc);

    var s = t.vtStream();
    defer s.deinit();
    s.nextSlice("https://a.com\r\n" ++ row);

    var state: terminal.RenderState = .empty;
    defer state.deinit(alloc);
    try state.update(alloc, &t);

    var set = try Set.fromConfig(alloc, &.{.{
        .regex = @import("../config/url.zig").regex,
        .action = .{ .open = {} },
        .highlight = .{ .always = {} },
    }});
    defer set.deinit(alloc);

    var result: terminal.RenderState.CellSet = .empty;
    defer result.deinit(alloc);
    try set.renderCellMap(
        alloc,
        &result,
        &state,
        null,
        .{},
    );

    // The ordinary URL before the pathological one is still matched.
    try testing.expect(result.contains(.{ .x = 0, .y = 0 }));
    try testing.expect(result.contains(.{ .x = 12, .y = 0 }));

    // The pathological URL is not highlighted at all. A budget overrun
    // skips the whole whitespace-delimited run it happened on, because
    // retrying it one codepoint at a time would spend a fresh budget on
    // the same expensive text once per byte.
    for (0..pathological.len) |x| {
        try testing.expect(!result.contains(.{ .x = @intCast(x), .y = 1 }));
    }

    // The space between the two links belongs to neither.
    try testing.expect(!result.contains(.{
        .x = pathological.len,
        .y = 1,
    }));

    // The link after it on the same row is still matched, because an
    // overrun skips the run it happened on instead of abandoning the rest
    // of the scan.
    try testing.expect(result.contains(.{
        .x = pathological.len + 1,
        .y = 1,
    }));
    try testing.expect(result.contains(.{
        .x = row.len - 1,
        .y = 1,
    }));
}

test "renderCellMap gives up after repeated regex budget overruns" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // Each of these rows costs a full retry budget to give up on. The
    // budget is shared by the whole scan, so once enough rows have burned
    // it the scan stops and the ordinary link on the last row is never
    // reached. Without a shared budget the scan would keep paying a fresh
    // budget for every row, which is how a viewport of this text turns a
    // bounded search into unbounded work per frame.
    const pathological = "https://x.com/" ++ ("." ** 40);
    const rows = scan_overrun_budget + 1;

    var t: terminal.Terminal = try .init(testing.io, alloc, .{
        .cols = pathological.len,
        .rows = @intCast(rows + 2),
    });
    defer t.deinit(alloc);

    var s = t.vtStream();
    defer s.deinit();
    s.nextSlice("https://a.com\r\n");
    for (0..rows) |_| s.nextSlice(pathological ++ "\r\n");
    s.nextSlice("https://b.com");

    var state: terminal.RenderState = .empty;
    defer state.deinit(alloc);
    try state.update(alloc, &t);

    var set = try Set.fromConfig(alloc, &.{.{
        .regex = @import("../config/url.zig").regex,
        .action = .{ .open = {} },
        .highlight = .{ .always = {} },
    }});
    defer set.deinit(alloc);

    var result: terminal.RenderState.CellSet = .empty;
    defer result.deinit(alloc);
    try set.renderCellMap(
        alloc,
        &result,
        &state,
        null,
        .{},
    );

    // The link before the pathological rows is matched.
    try testing.expect(result.contains(.{ .x = 0, .y = 0 }));

    // The link after them is not: the scan ran out of budget first.
    try testing.expect(!result.contains(.{
        .x = 0,
        .y = @intCast(rows + 1),
    }));
}

test "renderCellMap hover links" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var t: terminal.Terminal = try .init(testing.io, alloc, .{
        .cols = 5,
        .rows = 3,
    });
    defer t.deinit(alloc);

    var s = t.vtStream();
    defer s.deinit();
    const str = "1ABCD2EFGH\r\n3IJKL";
    s.nextSlice(str);

    var state: terminal.RenderState = .empty;
    defer state.deinit(alloc);
    try state.update(alloc, &t);

    // Get a set
    var set = try Set.fromConfig(alloc, &.{
        .{
            .regex = "AB",
            .action = .{ .open = {} },
            .highlight = .{ .hover = {} },
        },

        .{
            .regex = "EF",
            .action = .{ .open = {} },
            .highlight = .{ .always = {} },
        },
    });
    defer set.deinit(alloc);

    // Not hovering over the first link
    {
        var result: terminal.RenderState.CellSet = .empty;
        defer result.deinit(alloc);
        try set.renderCellMap(
            alloc,
            &result,
            &state,
            null,
            .{},
        );

        // Test our matches
        try testing.expect(!result.contains(.{ .x = 0, .y = 0 }));
        try testing.expect(!result.contains(.{ .x = 1, .y = 0 }));
        try testing.expect(!result.contains(.{ .x = 2, .y = 0 }));
        try testing.expect(!result.contains(.{ .x = 3, .y = 0 }));
        try testing.expect(result.contains(.{ .x = 1, .y = 1 }));
        try testing.expect(!result.contains(.{ .x = 1, .y = 2 }));
    }

    // Hovering over the first link
    {
        var result: terminal.RenderState.CellSet = .empty;
        defer result.deinit(alloc);
        try set.renderCellMap(
            alloc,
            &result,
            &state,
            .{ .x = 1, .y = 0 },
            .{},
        );

        // Test our matches
        try testing.expect(!result.contains(.{ .x = 0, .y = 0 }));
        try testing.expect(result.contains(.{ .x = 1, .y = 0 }));
        try testing.expect(result.contains(.{ .x = 2, .y = 0 }));
        try testing.expect(!result.contains(.{ .x = 3, .y = 0 }));
        try testing.expect(result.contains(.{ .x = 1, .y = 1 }));
        try testing.expect(!result.contains(.{ .x = 1, .y = 2 }));
    }
}

test "renderCellMap inactive links don't allocate" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    var t: terminal.Terminal = try .init(io, alloc, .{
        .cols = 5,
        .rows = 3,
    });
    defer t.deinit(alloc);

    var s = t.vtStream();
    defer s.deinit();
    const str = "1ABCD2EFGH\r\n3IJKL";
    s.nextSlice(str);

    var state: terminal.RenderState = .empty;
    defer state.deinit(alloc);
    try state.update(alloc, &t);

    var set = try Set.fromConfig(alloc, &.{
        .{
            .regex = "AB",
            .action = .{ .open = {} },
            .highlight = .{ .hover = {} },
        },

        .{
            .regex = "EF",
            .action = .{ .open = {} },
            .highlight = .{ .always_mods = .{ .ctrl = true } },
        },

        .{
            .regex = "IJ",
            .action = .{ .open = {} },
            .highlight = .{ .hover_mods = .{ .shift = true } },
        },
    });
    defer set.deinit(alloc);

    var failing = std.testing.FailingAllocator.init(
        alloc,
        .{ .fail_index = 0 },
    );
    const failing_alloc = failing.allocator();

    var result: terminal.RenderState.CellSet = .empty;
    defer result.deinit(failing_alloc);
    try set.renderCellMap(
        failing_alloc,
        &result,
        &state,
        null,
        .{},
    );

    try testing.expectEqual(@as(usize, 0), result.count());
}

test "renderCellMap mods no match" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var t: terminal.Terminal = try .init(testing.io, alloc, .{
        .cols = 5,
        .rows = 3,
    });
    defer t.deinit(alloc);

    var s = t.vtStream();
    defer s.deinit();
    const str = "1ABCD2EFGH\r\n3IJKL";
    s.nextSlice(str);

    var state: terminal.RenderState = .empty;
    defer state.deinit(alloc);
    try state.update(alloc, &t);

    // Get a set
    var set = try Set.fromConfig(alloc, &.{
        .{
            .regex = "AB",
            .action = .{ .open = {} },
            .highlight = .{ .always = {} },
        },

        .{
            .regex = "EF",
            .action = .{ .open = {} },
            .highlight = .{ .always_mods = .{ .ctrl = true } },
        },
    });
    defer set.deinit(alloc);

    // Get our matches
    var result: terminal.RenderState.CellSet = .empty;
    defer result.deinit(alloc);
    try set.renderCellMap(
        alloc,
        &result,
        &state,
        null,
        .{},
    );

    // Test our matches
    try testing.expect(!result.contains(.{ .x = 0, .y = 0 }));
    try testing.expect(result.contains(.{ .x = 1, .y = 0 }));
    try testing.expect(result.contains(.{ .x = 2, .y = 0 }));
    try testing.expect(!result.contains(.{ .x = 3, .y = 0 }));
    try testing.expect(!result.contains(.{ .x = 1, .y = 1 }));
    try testing.expect(!result.contains(.{ .x = 1, .y = 2 }));
}
