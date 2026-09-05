//! This structure represents the state required to render a terminal
//! grid using the font subsystem. It is "shared" because it is able to
//! be shared across multiple surfaces.
//!
//! It is desirable for the grid state to be shared because the font
//! configuration for a set of surfaces is almost always the same and
//! font data is relatively memory intensive. Further, the font subsystem
//! should be read-heavy compared to write-heavy, so it handles concurrent
//! reads well. Going even further, the font subsystem should be very rarely
//! read at all since it should only be necessary when the grid actively
//! changes.
//!
//! SharedGrid does NOT support resizing, font-family changes, font removals
//! in collections, etc. Because the Grid is shared this would cause a
//! major disruption in the rendering of multiple surfaces (i.e. increasing
//! the font size in one would increase it in all). In many cases this isn't
//! desirable so to implement configuration changes the grid should be
//! reinitialized and all surfaces should switch over to using that one.
const SharedGrid = @This();

const std = @import("std");
const assert = @import("../quirks.zig").inlineAssert;
const tripwire = @import("../tripwire.zig");
const Allocator = std.mem.Allocator;
const renderer = @import("../renderer.zig");
const font = @import("main.zig");
const Atlas = font.Atlas;
const CodepointResolver = font.CodepointResolver;
const Collection = font.Collection;
const Face = font.Face;
const Glyph = font.Glyph;
const Library = font.Library;
const Metrics = font.Metrics;
const Presentation = font.Presentation;
const Style = font.Style;
const RenderOptions = font.Glyph.RenderOptions;
const global = @import("../global.zig");

const log = std.log.scoped(.font_shared_grid);

/// Cache for codepoints to font indexes in a group.
codepoints: std.AutoHashMapUnmanaged(CodepointKey, ?Collection.Index) = .{},

/// Cache for glyph renders into the atlas.
glyphs: std.HashMapUnmanaged(GlyphKey, Render, GlyphKey.Context, 80) = .{},

/// The texture atlas to store renders in. The Glyph data in the glyphs
/// cache is dependent on the atlas matching.
atlas_grayscale: Atlas,
atlas_color: Atlas,

/// The underlying resolver for font data, fallbacks, etc. The shared
/// grid takes ownership of the resolver and will free it.
resolver: CodepointResolver,

/// The currently active grid metrics dictating the layout of the grid.
/// This is calculated based on the resolver and current fonts.
metrics: Metrics,

/// The RwLock used to protect the shared grid. Callers are expected to use
/// this directly if they need to i.e. access the atlas directly. Because
/// callers can use this lock directly, maintainers need to be extra careful
/// to review call sites to ensure they are using the lock correctly.
lock: std.Io.RwLock,

pub const init_tw = tripwire.module(enum {
    codepoints_capacity,
    glyphs_capacity,
    reload_metrics,
}, init);

pub const InitError = std.mem.Allocator.Error || Collection.UpdateMetricsError;

/// Initialize the grid.
///
/// The resolver must have a collection that supports deferred loading
/// (collection.load_options != null). This is because we need the load
/// options data to determine grid metrics and setup our sprite font.
///
/// SharedGrid always configures the sprite font. This struct is expected to be
/// used with a terminal grid and therefore the sprite font is always
/// necessary for correct rendering.
pub fn init(
    alloc: Allocator,
    resolver: CodepointResolver,
) InitError!SharedGrid {
    const tw = init_tw;

    // We need to support loading options since we use the size data
    assert(resolver.collection.load_options != null);

    var atlas_grayscale = try Atlas.init(alloc, 512, .grayscale);
    errdefer atlas_grayscale.deinit(alloc);
    var atlas_color = try Atlas.init(alloc, 512, .bgra);
    errdefer atlas_color.deinit(alloc);

    var result: SharedGrid = .{
        .resolver = resolver,
        .atlas_grayscale = atlas_grayscale,
        .atlas_color = atlas_color,
        .lock = .init,
        .metrics = undefined, // Loaded below
    };

    // We set an initial capacity that can fit a good number of characters.
    // This number was picked empirically based on my own terminal usage.
    try tw.check(.codepoints_capacity);
    try result.codepoints.ensureTotalCapacity(alloc, 128);
    errdefer result.codepoints.deinit(alloc);
    try tw.check(.glyphs_capacity);
    try result.glyphs.ensureTotalCapacity(alloc, 128);
    errdefer result.glyphs.deinit(alloc);

    // Initialize our metrics.
    try tw.check(.reload_metrics);
    try result.reloadMetrics();

    return result;
}

/// Deinit. Assumes no concurrent access so no lock is taken.
pub fn deinit(self: *SharedGrid, alloc: Allocator) void {
    self.codepoints.deinit(alloc);
    self.glyphs.deinit(alloc);
    self.atlas_grayscale.deinit(alloc);
    self.atlas_color.deinit(alloc);
    self.resolver.deinit(alloc);
}

fn reloadMetrics(self: *SharedGrid) !void {
    const collection = &self.resolver.collection;
    try collection.updateMetrics();

    self.metrics = collection.metrics.?;

    // Setup our sprite font.
    self.resolver.sprite = .{ .metrics = self.metrics };
}

/// Returns the grid cell size.
///
/// This is not thread safe.
pub fn cellSize(self: *SharedGrid) renderer.CellSize {
    return .{
        .width = self.metrics.cell_width,
        .height = self.metrics.cell_height,
    };
}

/// Set the size both atlases refuse to grow past, for a renderer that has
/// attached to this grid and knows what its device can hold.
///
/// The atlases are built before any renderer exists, so they start at a
/// ceiling low enough for every device we support. A backend that can ask
/// its device raises or lowers that here.
pub fn setMaxAtlasSize(self: *SharedGrid, size_max: u32) void {
    self.lock.lockUncancelable(global.io());
    defer self.lock.unlock(global.io());
    self.atlas_grayscale.setMaxSize(size_max);
    self.atlas_color.setMaxSize(size_max);
}

/// Get the font index for a given codepoint. This is cached.
///
/// This always forces loading any deferred fonts since we assume that if
/// you're looking up an index that the caller plans to use the font. By
/// loading the font in this function we can ensure thread-safety on the
/// load without complicating future calls.
pub fn getIndex(
    self: *SharedGrid,
    alloc: Allocator,
    cp: u32,
    style: Style,
    p: ?Presentation,
) !?Collection.Index {
    const key: CodepointKey = .{ .style = style, .codepoint = cp, .presentation = p };

    // Fast path: the cache has the value. This is almost always true and
    // only requires a read lock.
    {
        self.lock.lockSharedUncancelable(global.io());
        defer self.lock.unlockShared(global.io());
        if (self.codepoints.get(key)) |v| return v;
    }

    // Slow path: we need to search this codepoint
    self.lock.lockUncancelable(global.io());
    defer self.lock.unlock(global.io());

    // Try to get it, if it is now in the cache another thread beat us to it.
    const gop = try self.codepoints.getOrPut(alloc, key);
    if (gop.found_existing) return gop.value_ptr.*;
    errdefer self.codepoints.removeByPtr(gop.key_ptr);

    // Load a value and cache it. This even caches negative matches.
    const value = self.resolver.getIndex(alloc, cp, style, p);
    gop.value_ptr.* = value;

    if (value) |idx| preload: {
        // If the font is a sprite font then we don't need to preload
        // because getFace doesn't work with special fonts.
        if (idx.special() != null) break :preload;

        // Load the face in case its deferred. If this fails then we would've
        // failed to load it in the future anyways so we want to undo all
        // the caching we did.
        _ = try self.resolver.collection.getFace(idx);
    }

    return value;
}

/// Returns true if the given font index has the codepoint and presentation.
pub fn hasCodepoint(
    self: *SharedGrid,
    idx: Collection.Index,
    cp: u32,
    p: ?Presentation,
) bool {
    self.lock.lockSharedUncancelable(global.io());
    defer self.lock.unlockShared(global.io());
    return self.resolver.collection.hasCodepoint(
        idx,
        cp,
        if (p) |v| .{ .explicit = v } else .{ .any = {} },
    );
}

pub const Render = struct {
    glyph: Glyph,
    presentation: Presentation,
};

/// Render a codepoint. This uses the first font index that has the codepoint
/// and matches the presentation requested. If the codepoint cannot be found
/// in any font, an null render is returned.
pub fn renderCodepoint(
    self: *SharedGrid,
    alloc: Allocator,
    cp: u32,
    style: Style,
    p: ?Presentation,
    opts: RenderOptions,
) !?Render {
    // Note: we could optimize the below to use way less locking, but
    // at the time of writing this codepath is only called for preedit
    // text which is relatively rare and almost non-existent in multiple
    // surfaces at the same time.

    // Get the font that has the codepoint
    const index = try self.getIndex(alloc, cp, style, p) orelse return null;

    // Get the glyph for the font
    const glyph_index = glyph_index: {
        self.lock.lockSharedUncancelable(global.io());
        defer self.lock.unlockShared(global.io());
        const face = try self.resolver.collection.getFace(index);
        break :glyph_index face.glyphIndex(cp) orelse return null;
    };

    // Render
    return try self.renderGlyph(alloc, index, glyph_index, opts);
}

pub const renderGlyph_tw = tripwire.module(enum {
    get_presentation,
}, renderGlyph);

// FIXME: This gets a massive manual error set for now because there is an
// equally massive call stack in the get_presentation tracepoint. This should
// be consolidated into something better ASAP.
pub const RenderGlyphError = error{
    ArrayTooLarge,
    AtlasFull,
    BadArgument,
    BbxTooBig,
    BitmapHandlingError,
    CMapTableMissing,
    CannotOpenResource,
    CannotOpenStream,
    CannotRenderGlyph,
    CodeOverflow,
    CorruptedFontGlyphs,
    CorruptedFontHeader,
    CouldNotFindContext,
    DebugOpCode,
    DeferredLoadingUnavailable,
    // Raised by the Windows directwrite_freetype backend's face lookup.
    DirectWriteError,
    DivideByZero,
    ENDFInExecStream,
    ExecutionTooLong,
    FontHasNoFile,
    FontPathCantDecode,
    FontconfigFailed,
    FontconfigNoId,
    FontconfigNoMatch,
    FontconfigTypeMismatch,
    GlyphResizeFailed,
    HarfbuzzFailed,
    HmtxTableMissing,
    HorizHeaderMissing,
    Ignore,
    IndexOutOfBounds,
    InvalidArgument,
    InvalidAtlasFormat,
    InvalidCacheHandle,
    InvalidCharMapFormat,
    InvalidCharMapHandle,
    InvalidCharacterCode,
    InvalidCodeRange,
    InvalidComposite,
    InvalidDriverHandle,
    InvalidFaceHandle,
    InvalidFileFormat,
    InvalidFrameOperation,
    InvalidFrameRead,
    InvalidGlyphFormat,
    InvalidGlyphIndex,
    InvalidHandle,
    InvalidHeight,
    InvalidHorizMetrics,
    InvalidLibraryHandle,
    InvalidMatrix,
    InvalidOffset,
    InvalidOpcode,
    InvalidOutline,
    InvalidPPem,
    InvalidPixelSize,
    InvalidPostTable,
    InvalidPostTableFormat,
    InvalidReference,
    InvalidSVGTable,
    InvalidSizeHandle,
    InvalidSlotHandle,
    InvalidState,
    InvalidStreamHandle,
    InvalidStreamOperation,
    InvalidStreamRead,
    InvalidStreamSeek,
    InvalidStreamSkip,
    InvalidTable,
    InvalidVersion,
    InvalidVertMetrics,
    InvalidWidth,
    LocationsMissing,
    LowerModuleVersion,
    MathError,
    MissingBbxField,
    MissingCharsField,
    MissingEncodingField,
    MissingFontField,
    MissingFontboundingboxField,
    MissingModule,
    MissingProperty,
    MissingSizeField,
    MissingStartcharField,
    MissingStartfontField,
    NameTableMissing,
    NestedDEFS,
    NestedFrameAccess,
    NoCurrentPoint,
    NoUnicodeGlyphName,
    OutOfMemory,
    PathNotClosed,
    PixelSourceNotPreMultiplied,
    PostTableMissing,
    RasterCorrupted,
    RasterNegativeHeight,
    RasterOverflow,
    RasterUninitialized,
    SpecialHasNoFace,
    StackOverflow,
    StackUnderflow,
    Syntax,
    TableMissing,
    TooFewArguments,
    TooManyCaches,
    TooManyDrivers,
    TooManyExtensions,
    TooManyFunctionDefs,
    TooManyHints,
    TooManyInstructionDefs,
    UnimplementedFeature,
    UnknownFileFormat,
    UnknownFreetypeError,
    UnlistedObject,
    UnsupportedGlyphFormat,
    WrongAtlas,
};

/// Render a glyph index. This automatically determines the correct texture
/// atlas to use and caches the result.
pub fn renderGlyph(
    self: *SharedGrid,
    alloc: Allocator,
    index: Collection.Index,
    glyph_index: u32,
    opts: RenderOptions,
) RenderGlyphError!Render {
    const tw = renderGlyph_tw;

    const key: GlyphKey = .{ .index = index, .glyph = glyph_index, .opts = opts };

    // Fast path: the cache has the value. This is almost always true and
    // only requires a read lock.
    {
        self.lock.lockSharedUncancelable(global.io());
        defer self.lock.unlockShared(global.io());
        if (self.glyphs.get(key)) |v| return v;
    }

    // Slow path: we need to search this codepoint
    self.lock.lockUncancelable(global.io());
    defer self.lock.unlock(global.io());

    const gop = try self.glyphs.getOrPut(alloc, key);
    if (gop.found_existing) return gop.value_ptr.*;

    // Cleared when the atlas reset below drops the whole cache, taking our
    // reserved slot with it. After that `gop` dangles and must not be used.
    var slot_valid = true;
    errdefer if (slot_valid) self.glyphs.removeByPtr(gop.key_ptr);

    // Get the presentation to determine what atlas to use
    try tw.check(.get_presentation);
    const p = try self.resolver.getPresentation(index, glyph_index);
    const atlas: *font.Atlas = switch (p) {
        .text => &self.atlas_grayscale,
        .emoji => &self.atlas_color,
    };

    var render_opts = opts;

    // Always use these constraints for emoji.
    if (p == .emoji) {
        render_opts.constraint = .{
            // Scale emoji to be as large as possible
            // while preserving their aspect ratio.
            .size = .cover,

            // Center the emoji in its cells.
            .align_horizontal = .center,
            .align_vertical = .center,

            // Add a small bit of padding so the emoji
            // doesn't quite touch the edges of the cells.
            .pad_left = 0.025,
            .pad_right = 0.025,
        };
    }

    // Render into the atlas
    const glyph = self.resolver.renderGlyph(
        alloc,
        atlas,
        index,
        glyph_index,
        render_opts,
    ) catch |err| switch (err) {
        // If the atlas is full, we make room and try once more.
        error.AtlasFull => blk: {
            atlas.grow(alloc, atlas.size * 2) catch |grow_err| switch (grow_err) {
                // The atlas is as large as the GPU can hold, so growing
                // it any further would only produce a texture that fails
                // to upload from now on. Start over with an empty atlas
                // instead. Every render cached in it points into the old
                // layout, so those go with it; users of the atlas watch
                // its generation to drop their own caches.
                error.AtlasTooLarge => {
                    log.warn(
                        "atlas full at its maximum size, resetting size={} max={}",
                        .{ atlas.size, atlas.max_size },
                    );
                    atlas.reset();

                    // Our own slot goes first: its value is still
                    // undefined, so the sweep below must not read it.
                    self.glyphs.removeByPtr(gop.key_ptr);
                    slot_valid = false;

                    // The other atlas was not reset, so its renders stay
                    // valid. If we cannot afford the list of keys to drop
                    // we drop everything, which is correct, just wasteful.
                    self.dropRenders(alloc, p) catch
                        self.glyphs.clearRetainingCapacity();
                },

                else => |e| return e,
            };

            break :blk try self.resolver.renderGlyph(
                alloc,
                atlas,
                index,
                glyph_index,
                render_opts,
            );
        },

        else => return err,
    };

    const render: Render = .{
        .glyph = glyph,
        .presentation = p,
    };

    // Cache and return. A reset above invalidated our reserved slot, so
    // in that case we insert into the now empty cache from scratch.
    if (!slot_valid) {
        try self.glyphs.put(alloc, key, render);
        return render;
    }

    gop.value_ptr.* = render;
    return gop.value_ptr.*;
}

/// Drop every cached render that lives in the atlas for presentation `p`.
///
/// Removing an entry can move a later one into a slot the iterator has
/// already passed, so the keys are collected before any of them is removed.
///
/// Caller must hold the write lock.
fn dropRenders(
    self: *SharedGrid,
    alloc: Allocator,
    p: Presentation,
) Allocator.Error!void {
    var stale: std.ArrayList(GlyphKey) = .empty;
    defer stale.deinit(alloc);

    var it = self.glyphs.iterator();
    while (it.next()) |entry| {
        if (entry.value_ptr.presentation != p) continue;
        try stale.append(alloc, entry.key_ptr.*);
    }

    for (stale.items) |stale_key| _ = self.glyphs.remove(stale_key);
}

const CodepointKey = struct {
    style: Style,
    codepoint: u32,
    presentation: ?Presentation,
};

const GlyphKey = struct {
    index: Collection.Index,
    glyph: u32,
    opts: RenderOptions,

    const Context = struct {
        pub fn hash(_: Context, key: GlyphKey) u64 {
            // Packed is a u64 but std.hash.int improves uniformity and
            // avoids collisions in our hashmap.
            const packed_key = Packed.from(key);
            return std.hash.int(@as(u64, @bitCast(packed_key)));
        }

        pub fn eql(_: Context, a: GlyphKey, b: GlyphKey) bool {
            // Packed checks glyphs but in most cases the glyphs are NOT
            // equal so the first check leads to increased throughput.
            return a.glyph == b.glyph and Packed.from(a) == Packed.from(b);
        }
    };

    const Packed = packed struct(u64) {
        index: Collection.Index,
        glyph: u32,
        opts: packed struct(u16) {
            cell_width: u2,
            thicken: bool,
            thicken_strength: u8,
            constraint_width: u2,
            _padding: u3 = 0,
        },

        inline fn from(key: GlyphKey) Packed {
            return .{
                .index = key.index,
                .glyph = key.glyph,
                .opts = .{
                    .cell_width = key.opts.cell_width orelse 0,
                    .thicken = key.opts.thicken,
                    .thicken_strength = key.opts.thicken_strength,
                    .constraint_width = key.opts.constraint_width,
                },
            };
        }
    };
};

const TestMode = enum { normal };

fn testGrid(mode: TestMode, alloc: Allocator, lib: Library) !SharedGrid {
    const testFont = font.embedded.regular;

    var c = Collection.init();
    c.load_options = .{ .library = lib };

    switch (mode) {
        .normal => {
            _ = try c.add(alloc, try .init(
                lib,
                testFont,
                .{ .size = .{ .points = 12, .xdpi = 96, .ydpi = 96 } },
            ), .{
                .style = .regular,
                .fallback = false,
                .size_adjustment = .none,
            });
        },
    }

    var r: CodepointResolver = .{ .collection = c };
    errdefer r.deinit(alloc);

    return try init(alloc, r);
}

test getIndex {
    const testing = std.testing;
    const alloc = testing.allocator;
    // const testEmoji = @import("test.zig").fontEmoji;

    var lib = try Library.init(alloc);
    defer lib.deinit();

    var grid = try testGrid(.normal, alloc, lib);
    defer grid.deinit(alloc);

    // Visible ASCII.
    for (32..127) |i| {
        const idx = (try grid.getIndex(alloc, @intCast(i), .regular, null)).?;
        try testing.expectEqual(Style.regular, idx.style);
        try testing.expectEqual(@as(Collection.Index.IndexInt, 0), idx.idx);
        try testing.expect(grid.hasCodepoint(idx, @intCast(i), null));
    }

    // Do it again without a resolver set to ensure we only hit the cache
    const old_resolver = grid.resolver;
    grid.resolver = undefined;
    defer grid.resolver = old_resolver;
    for (32..127) |i| {
        const idx = (try grid.getIndex(alloc, @intCast(i), .regular, null)).?;
        try testing.expectEqual(Style.regular, idx.style);
        try testing.expectEqual(@as(Collection.Index.IndexInt, 0), idx.idx);
    }
}

test "renderGlyph error after cache insert rolls back cache entry" {
    // This test verifies that when renderGlyph fails after inserting a cache
    // entry (via getOrPut), the errdefer properly removes the entry, preventing
    // corrupted/uninitialized data from remaining in the cache.

    const testing = std.testing;
    const alloc = testing.allocator;

    var lib = try Library.init(alloc);
    defer lib.deinit();

    var grid = try testGrid(.normal, alloc, lib);
    defer grid.deinit(alloc);

    // Get the font index for 'A'
    const idx = (try grid.getIndex(alloc, 'A', .regular, null)).?;

    // Get the glyph index for 'A'
    const glyph_index = glyph_index: {
        grid.lock.lockSharedUncancelable(testing.io);
        defer grid.lock.unlockShared(testing.io);
        const face = try grid.resolver.collection.getFace(idx);
        break :glyph_index face.glyphIndex('A').?;
    };

    const render_opts: RenderOptions = .{ .grid_metrics = grid.metrics };
    const key: GlyphKey = .{ .index = idx, .glyph = glyph_index, .opts = render_opts };

    // Verify the cache is empty for this glyph
    try testing.expect(grid.glyphs.get(key) == null);

    // Set up tripwire to fail after cache insert.
    // We use OutOfMemory as it's a valid error in the renderGlyph error set.
    const tw = renderGlyph_tw;
    defer tw.end(.reset) catch {};
    tw.errorAlways(.get_presentation, error.OutOfMemory);

    // This should fail due to the tripwire
    try testing.expectError(
        error.OutOfMemory,
        grid.renderGlyph(alloc, idx, glyph_index, render_opts),
    );

    // The errdefer should have removed the cache entry, leaving the cache clean.
    // Without the errdefer fix, this would contain garbage/uninitialized data.
    try testing.expect(grid.glyphs.get(key) == null);
}

test "renderGlyph resets the Atlas when it can no longer grow" {
    const testing = std.testing;
    const alloc = testing.allocator;

    var lib = try Library.init(alloc);
    defer lib.deinit();

    var grid = try testGrid(.normal, alloc, lib);
    defer grid.deinit(alloc);

    // Swap in a tiny grayscale atlas that is already at its maximum size, so
    // it fills up quickly and has nowhere to grow into.
    grid.atlas_grayscale.deinit(alloc);
    grid.atlas_grayscale = try Atlas.init(alloc, 64, .grayscale);
    grid.atlas_grayscale.max_size = 64;

    const render_opts: RenderOptions = .{ .grid_metrics = grid.metrics };

    // A cached render that lives in the color atlas, which the grayscale
    // reset must not touch. Faked so this doesn't depend on an emoji font
    // being installed.
    const color_key: GlyphKey = .{
        .index = (try grid.getIndex(alloc, 'A', .regular, null)).?,
        .glyph = std.math.maxInt(u32),
        .opts = render_opts,
    };
    try grid.glyphs.put(alloc, color_key, .{
        .glyph = std.mem.zeroes(Glyph),
        .presentation = .emoji,
    });

    // Render visible ASCII. Each render must succeed: once the atlas fills up
    // it is emptied and started over rather than failing from here on.
    var rendered: usize = 0;
    for (32..127) |i| {
        const cp: u21 = @intCast(i);
        const idx = (try grid.getIndex(alloc, cp, .regular, null)).?;
        const glyph_index = glyph_index: {
            grid.lock.lockSharedUncancelable(testing.io);
            defer grid.lock.unlockShared(testing.io);
            const face = try grid.resolver.collection.getFace(idx);
            break :glyph_index face.glyphIndex(cp) orelse continue;
        };
        _ = try grid.renderGlyph(alloc, idx, glyph_index, render_opts);
        rendered += 1;
    }

    // The atlas was emptied at least once and the stale renders went with it.
    try testing.expect(grid.atlas_grayscale.generation.load(.monotonic) > 0);
    try testing.expect(grid.glyphs.count() < rendered);

    // The color atlas never reset, so its render is still cached.
    try testing.expect(grid.glyphs.get(color_key) != null);
}

test "init error" {
    // Test every failure point in `init` and ensure that we don't
    // leak memory (testing.allocator verifies) since we're exiting early.
    //
    // BUG: Currently this test will fail because init() is missing errdefer
    // cleanup for codepoints and glyphs when late operations fail
    // (ensureTotalCapacity, reloadMetrics).
    const testing = std.testing;
    const alloc = testing.allocator;

    for (std.meta.tags(init_tw.FailPoint)) |tag| {
        const tw = init_tw;
        defer tw.end(.reset) catch unreachable;
        tw.errorAlways(tag, error.OutOfMemory);

        // Create a resolver for testing - we need to set up a minimal one.
        // The caller is responsible for cleaning up the resolver if init fails.
        var lib = try Library.init(alloc);
        defer lib.deinit();

        var c = Collection.init();
        c.load_options = .{ .library = lib };
        _ = try c.add(alloc, try .init(
            lib,
            font.embedded.regular,
            .{ .size = .{ .points = 12, .xdpi = 96, .ydpi = 96 } },
        ), .{
            .style = .regular,
            .fallback = false,
            .size_adjustment = .none,
        });

        var resolver: CodepointResolver = .{ .collection = c };
        defer resolver.deinit(alloc); // Caller cleans up on init failure

        try testing.expectError(
            error.OutOfMemory,
            init(alloc, resolver),
        );
    }
}
