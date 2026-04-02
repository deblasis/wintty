//! A deferred face represents a single font face with all the information
//! necessary to load it, but defers loading the full face until it is
//! needed.
//!
//! This allows us to have many fallback fonts to look for glyphs, but
//! only load them if they're really needed.
const DeferredFace = @This();

const std = @import("std");
const Allocator = std.mem.Allocator;
const fontconfig = @import("fontconfig");
const macos = @import("macos");
const dwrite = @import("directwrite.zig");
const font = @import("main.zig");
const options = @import("main.zig").options;
const Library = @import("main.zig").Library;
const Face = @import("main.zig").Face;
const Presentation = @import("main.zig").Presentation;

const log = std.log.scoped(.deferred_face);

/// Fontconfig
fc: if (options.backend == .fontconfig_freetype) ?Fontconfig else void =
    if (options.backend == .fontconfig_freetype) null else {},

/// CoreText
ct: if (font.Discover == font.discovery.CoreText) ?CoreText else void =
    if (font.Discover == font.discovery.CoreText) null else {},

/// Windows (FreeType directory scan)
win: if (options.backend == .freetype_windows) ?Windows else void =
    if (options.backend == .freetype_windows) null else {},

/// Canvas
wc: if (options.backend == .web_canvas) ?WebCanvas else void =
    if (options.backend == .web_canvas) null else {},

/// DirectWrite
dw: if (options.backend == .directwrite_freetype) ?DirectWrite else void =
    if (options.backend == .directwrite_freetype) null else {},

/// Fontconfig specific data. This is only present if building with fontconfig.
pub const Fontconfig = struct {
    /// The pattern for this font. This must be the "render prepared" pattern.
    /// (i.e. call FcFontRenderPrepare).
    pattern: *fontconfig.Pattern,

    /// Charset and Langset are used for quick lookup if a codepoint and
    /// presentation style are supported. They can be derived from pattern
    /// but are cached since they're frequently used.
    charset: *const fontconfig.CharSet,
    langset: *const fontconfig.LangSet,

    /// Variations to apply to this font.
    variations: []const font.face.Variation,

    pub fn deinit(self: *Fontconfig) void {
        self.pattern.destroy();
        self.* = undefined;
    }
};

/// Windows specific data. Only present with the freetype_windows backend.
///
/// Unlike Fontconfig/CoreText which carry lightweight descriptor handles,
/// the Windows backend has no external descriptor service — the "deferred"
/// metadata is the FreeType face itself. We keep a pre-loaded face (loaded
/// at discovery time) to answer `hasCodepoint` cheaply without re-opening
/// the file on every query, and remember the path so `load()` can open a
/// fresh face at the caller's requested size/options.
pub const Windows = struct {
    /// Path to the font file. Owned here.
    path: [:0]const u8,

    /// Face index within the file (for .ttc collections).
    face_index: i32,

    /// Variations to apply on load.
    variations: []const font.face.Variation,

    /// Pre-loaded face used for cheap metadata queries (glyphIndex,
    /// hasColor). The size it was opened at is irrelevant for these
    /// queries since the CMap is size-independent. Deinit'd with us.
    peek: Face,

    /// Whether the face presents as emoji (has color glyphs) or text.
    presentation: Presentation,

    /// Allocator that owns `path`.
    alloc: Allocator,

    pub fn deinit(self: *Windows) void {
        self.peek.deinit();
        self.alloc.free(self.path);
        self.* = undefined;
    }
};

/// CoreText specific data. This is only present when building with CoreText.
pub const CoreText = struct {
    /// The initialized font
    font: *macos.text.Font,

    /// Variations to apply to this font. We apply the variations to the
    /// search descriptor but sometimes when the font collection is
    /// made the variation axes are reset so we have to reapply them.
    variations: []const font.face.Variation,

    pub fn deinit(self: *CoreText) void {
        self.font.release();
        self.* = undefined;
    }
};

/// WebCanvas specific data. This is only present when building with canvas.
pub const WebCanvas = struct {
    /// The allocator to use for fonts
    alloc: Allocator,

    /// The string to use for the "font" attribute for the canvas
    font_str: [:0]const u8,

    /// The presentation for this font.
    presentation: Presentation,

    pub fn deinit(self: *WebCanvas) void {
        self.alloc.free(self.font_str);
        self.* = undefined;
    }
};

pub const DirectWrite = struct {
    font: *dwrite.IDWriteFont,
    variations: []const font.face.Variation,

    pub fn deinit(self: *DirectWrite) void {
        _ = self.font.Release();
        self.* = undefined;
    }
};

pub fn deinit(self: *DeferredFace) void {
    switch (options.backend) {
        .fontconfig_freetype => if (self.fc) |*fc| fc.deinit(),
        .freetype => {},
        .freetype_windows => if (self.win) |*w| w.deinit(),
        .directwrite_freetype => if (self.dw) |*dw_| dw_.deinit(),
        .web_canvas => if (self.wc) |*wc| wc.deinit(),
        .coretext,
        .coretext_freetype,
        .coretext_harfbuzz,
        .coretext_noshape,
        => if (self.ct) |*ct| ct.deinit(),
    }
    self.* = undefined;
}

/// Returns the family name of the font.
pub fn familyName(self: DeferredFace, buf: []u8) ![]const u8 {
    switch (options.backend) {
        .freetype => {},

        .freetype_windows => if (self.win) |w| return try w.peek.name(buf),

        .directwrite_freetype => if (self.dw) |dw_| {
            var names: ?*dwrite.IDWriteLocalizedStrings = null;
            var str_exists: i32 = 0;
            const hr = dw_.font.GetInformationalStrings(.WIN32_FAMILY_NAMES, &names, &str_exists);
            if (dwrite.SUCCEEDED(hr) and str_exists != 0) {
                if (names) |n| {
                    defer _ = n.Release();
                    return dwrite.getLocalizedString(n, buf);
                }
            }
            return "";
        },

        .fontconfig_freetype => if (self.fc) |fc|
            return (try fc.pattern.get(.family, 0)).string,

        .coretext,
        .coretext_freetype,
        .coretext_harfbuzz,
        .coretext_noshape,
        => if (self.ct) |ct| {
            const family_name = ct.font.copyAttribute(.family_name) orelse
                return "unknown";
            return family_name.cstringPtr(.utf8) orelse unsupported: {
                break :unsupported family_name.cstring(buf, .utf8) orelse
                    return error.OutOfMemory;
            };
        },

        .web_canvas => if (self.wc) |wc| return wc.font_str,
    }

    return "";
}

/// Returns the name of this face. The memory is always owned by the
/// face so it doesn't have to be freed.
pub fn name(self: DeferredFace, buf: []u8) ![]const u8 {
    switch (options.backend) {
        .freetype => {},

        .freetype_windows => if (self.win) |w| return try w.peek.name(buf),

        .directwrite_freetype => if (self.dw) |dw_| {
            // Try full name first
            var names: ?*dwrite.IDWriteLocalizedStrings = null;
            var str_exists: i32 = 0;
            var hr = dw_.font.GetInformationalStrings(.FULL_NAME, &names, &str_exists);
            if (dwrite.SUCCEEDED(hr) and str_exists != 0) {
                if (names) |n| {
                    defer _ = n.Release();
                    return dwrite.getLocalizedString(n, buf);
                }
            }
            // Fall back to face names
            var face_names: ?*dwrite.IDWriteLocalizedStrings = null;
            hr = dw_.font.GetFaceNames(&face_names);
            if (dwrite.SUCCEEDED(hr)) {
                if (face_names) |n| {
                    defer _ = n.Release();
                    return dwrite.getLocalizedString(n, buf);
                }
            }
            return "";
        },

        .fontconfig_freetype => if (self.fc) |fc|
            return (try fc.pattern.get(.fullname, 0)).string,

        .coretext,
        .coretext_freetype,
        .coretext_harfbuzz,
        .coretext_noshape,
        => if (self.ct) |ct| {
            const display_name = ct.font.copyDisplayName();
            return display_name.cstringPtr(.utf8) orelse unsupported: {
                // "NULL if the internal storage of theString does not allow
                // this to be returned efficiently." In this case, we need
                // to allocate. But we can't return an allocated string because
                // we don't have an allocator. Let's use the stack and log it.
                break :unsupported display_name.cstring(buf, .utf8) orelse
                    return error.OutOfMemory;
            };
        },

        .web_canvas => if (self.wc) |wc| return wc.font_str,
    }

    return "";
}

/// Load the deferred font face. This does nothing if the face is loaded.
pub fn load(
    self: *DeferredFace,
    lib: Library,
    opts: font.face.Options,
) !Face {
    return switch (options.backend) {
        .fontconfig_freetype => try self.loadFontconfig(lib, opts),
        .freetype_windows => try self.loadWindows(lib, opts),
        .directwrite_freetype => try self.loadDirectWrite(lib, opts),
        .coretext, .coretext_harfbuzz, .coretext_noshape => try self.loadCoreText(lib, opts),
        .coretext_freetype => try self.loadCoreTextFreetype(lib, opts),
        .web_canvas => try self.loadWebCanvas(opts),

        // Unreachable because we must be already loaded or have the
        // proper configuration for one of the other deferred mechanisms.
        .freetype => unreachable,
    };
}

fn loadFontconfig(
    self: *DeferredFace,
    lib: Library,
    opts: font.face.Options,
) !Face {
    const fc = self.fc.?;

    // Filename and index for our face so we can load it
    const filename = (try fc.pattern.get(.file, 0)).string;
    const face_index = (try fc.pattern.get(.index, 0)).integer;

    var face = try Face.initFile(lib, filename, face_index, opts);
    errdefer face.deinit();
    try face.setVariations(fc.variations, opts);
    return face;
}

fn loadWindows(
    self: *DeferredFace,
    lib: Library,
    opts: font.face.Options,
) !Face {
    const w = self.win.?;

    var face = try Face.initFile(lib, w.path, w.face_index, opts);
    errdefer face.deinit();
    try face.setVariations(w.variations, opts);
    return face;
}

fn loadCoreText(
    self: *DeferredFace,
    lib: Library,
    opts: font.face.Options,
) !Face {
    _ = lib;
    const ct = self.ct.?;
    var face = try Face.initFontCopy(ct.font, opts);
    errdefer face.deinit();
    try face.setVariations(ct.variations, opts);
    return face;
}

fn loadCoreTextFreetype(
    self: *DeferredFace,
    lib: Library,
    opts: font.face.Options,
) !Face {
    const ct = self.ct.?;

    // Get the URL for the font so we can get the filepath
    const url = ct.font.copyAttribute(.url) orelse
        return error.FontHasNoFile;
    defer url.release();

    // Get the path from the URL
    const path = url.copyPath() orelse return error.FontHasNoFile;
    defer path.release();

    // URL decode the path
    const blank = try macos.foundation.String.createWithBytes("", .utf8, false);
    defer blank.release();
    const decoded = try macos.foundation.URL.createStringByReplacingPercentEscapes(
        path,
        blank,
    );
    defer decoded.release();

    // Decode into a c string. 1024 bytes should be enough for anybody.
    var buf: [1024]u8 = undefined;
    const path_slice = decoded.cstring(buf[0..1023], .utf8) orelse
        return error.FontPathCantDecode;

    // Freetype requires null-terminated. We always leave space at
    // the end for a zero so we set that up here.
    buf[path_slice.len] = 0;

    // Face index 0 is not always correct. We don't ship this configuration
    // in a release build. Users should use the pure CoreText builds.
    //std.log.warn("path={s}", .{path_slice});
    var face = try Face.initFile(lib, buf[0..path_slice.len :0], 0, opts);
    errdefer face.deinit();
    try face.setVariations(ct.variations, opts);

    return face;
}

fn loadWebCanvas(
    self: *DeferredFace,
    opts: font.face.Options,
) !Face {
    const wc = self.wc.?;
    return try .initNamed(wc.alloc, wc.font_str, opts, wc.presentation);
}

fn loadDirectWrite(self: *DeferredFace, lib: Library, opts: font.face.Options) !Face {
    const dw_ = self.dw.?;

    var dw_face: ?*dwrite.IDWriteFontFace = null;
    var hr = dw_.font.CreateFontFace(&dw_face);
    if (dwrite.FAILED(hr)) return error.DirectWriteError;
    defer _ = dw_face.?.Release();

    // Get file count
    var num_files: u32 = 0;
    hr = dw_face.?.GetFiles(&num_files, null);
    if (dwrite.FAILED(hr) or num_files == 0) return error.FontHasNoFile;

    // Get first font file
    var font_file: ?*dwrite.IDWriteFontFile = null;
    var one: u32 = 1;
    hr = dw_face.?.GetFiles(&one, @ptrCast(&font_file));
    if (dwrite.FAILED(hr)) return error.FontHasNoFile;
    defer _ = font_file.?.Release();

    // Get reference key
    var key: ?*const anyopaque = null;
    var key_size: u32 = 0;
    hr = font_file.?.GetReferenceKey(&key, &key_size);
    if (dwrite.FAILED(hr)) return error.FontHasNoFile;

    // Get loader and QI to local loader
    var loader: ?*dwrite.IDWriteFontFileLoader = null;
    hr = font_file.?.GetLoader(&loader);
    if (dwrite.FAILED(hr)) return error.FontHasNoFile;
    defer _ = loader.?.Release();

    var local_loader_raw: ?*anyopaque = null;
    hr = loader.?.QueryInterface(&dwrite.IDWriteLocalFontFileLoader.IID, &local_loader_raw);
    if (dwrite.FAILED(hr)) return error.FontHasNoFile;
    const local_loader: *dwrite.IDWriteLocalFontFileLoader = @ptrCast(@alignCast(local_loader_raw.?));
    defer _ = local_loader.Release();

    // Get file path length then path
    var path_len: u32 = 0;
    hr = local_loader.GetFilePathLengthFromKey(key.?, key_size, &path_len);
    if (dwrite.FAILED(hr)) return error.FontHasNoFile;

    var wpath_buf: [512]u16 = undefined;
    if (path_len + 1 > wpath_buf.len) return error.FontPathCantDecode;
    hr = local_loader.GetFilePathFromKey(key.?, key_size, &wpath_buf, path_len + 1);
    if (dwrite.FAILED(hr)) return error.FontHasNoFile;

    // Convert UTF-16 path to null-terminated UTF-8 for FreeType
    var path_buf: [1024]u8 = undefined;
    const utf8_len = std.unicode.utf16LeToUtf8(path_buf[0 .. path_buf.len - 1], wpath_buf[0..path_len]) catch
        return error.FontPathCantDecode;
    path_buf[utf8_len] = 0;
    const path: [:0]const u8 = path_buf[0..utf8_len :0];

    const face_index: i32 = @intCast(dw_face.?.GetIndex());

    var face = try Face.initFile(lib, path, face_index, opts);
    errdefer face.deinit();
    try face.setVariations(dw_.variations, opts);
    return face;
}

/// Returns true if this face can satisfy the given codepoint and
/// presentation. If presentation is null, then it just checks if the
/// codepoint is present at all.
///
/// This should not require the face to be loaded IF we're using a
/// discovery mechanism (i.e. fontconfig). If no discovery is used,
/// the face is always expected to be loaded.
pub fn hasCodepoint(self: DeferredFace, cp: u32, p: ?Presentation) bool {
    switch (options.backend) {
        .fontconfig_freetype => {
            // If we are using fontconfig, use the fontconfig metadata to
            // avoid loading the face.
            if (self.fc) |fc| {
                // Check if char exists
                if (!fc.charset.hasChar(cp)) return false;

                // If we have a presentation, check it matches
                if (p) |desired| {
                    const emoji_lang = "und-zsye";
                    const actual: Presentation = if (fc.langset.hasLang(emoji_lang))
                        .emoji
                    else
                        .text;

                    return desired == actual;
                }

                return true;
            }
        },

        .freetype_windows => {
            // Use the pre-loaded peek face for a cheap CMap lookup.
            if (self.win) |w| {
                if (p) |desired| if (w.presentation != desired) return false;
                return w.peek.glyphIndex(cp) != null;
            }
        },

        .coretext,
        .coretext_freetype,
        .coretext_harfbuzz,
        .coretext_noshape,
        => {
            // If we are using coretext, we check the loaded CT font.
            if (self.ct) |ct| {
                // This presentation check isn't as detailed as isColorGlyph
                // because forced presentation modes are only used for emoji and
                // emoji should always have color glyphs set. This can be
                // more correct by using the isColorGlyph logic but I'd want
                // to find a font that actually requires this so we can write
                // a test for it before changing it.
                if (p) |desired_p| {
                    const traits = ct.font.getSymbolicTraits();
                    const actual_p: Presentation = if (traits.color_glyphs) .emoji else .text;
                    if (actual_p != desired_p) return false;
                }

                // Turn UTF-32 into UTF-16 for CT API
                var unichars: [2]u16 = undefined;
                const pair = macos.foundation.stringGetSurrogatePairForLongCharacter(cp, &unichars);
                const len: usize = if (pair) 2 else 1;

                // Get our glyphs
                var glyphs = [2]macos.graphics.Glyph{ 0, 0 };
                return ct.font.getGlyphsForCharacters(unichars[0..len], glyphs[0..len]);
            }
        },

        // Canvas always has the codepoint because we have no way of
        // really checking and we let the browser handle it.
        .directwrite_freetype => {
            if (self.dw) |dw_| {
                if (p) |desired_p| {
                    const is_color = dw_.font.IsColorFont() != 0;
                    const actual_p: Presentation = if (is_color) .emoji else .text;
                    if (actual_p != desired_p) return false;
                }
                var cp_exists: i32 = 0;
                const hr = dw_.font.HasCharacter(cp, &cp_exists);
                return dwrite.SUCCEEDED(hr) and cp_exists != 0;
            }
        },

        .web_canvas => if (self.wc) |wc| {
            // Fast-path if we have a specific presentation and we
            // don't match, then it is definitely not this face.
            if (p) |desired| if (wc.presentation != desired) return false;

            // Slow-path: we initialize the font, render it, and check
            // if it works and the presentation matches.
            var face = Face.initNamed(
                wc.alloc,
                wc.font_str,
                .{ .points = 12 },
                wc.presentation,
            ) catch |err| {
                log.warn("failed to init face for codepoint check " ++
                    "face={s} err={}", .{
                    wc.font_str,
                    err,
                });

                return false;
            };
            defer face.deinit();
            return face.glyphIndex(cp) != null;
        },

        .freetype => {},
    }

    // This is unreachable because discovery mechanisms terminate, and
    // if we're not using a discovery mechanism, the face MUST be loaded.
    unreachable;
}

/// The wasm-compatible API.
pub const Wasm = struct {
    const wasm = @import("../os/wasm.zig");
    const alloc = wasm.alloc;

    export fn deferred_face_new(ptr: [*]const u8, len: usize, presentation: u16) ?*DeferredFace {
        return deferred_face_new_(ptr, len, presentation) catch |err| {
            log.warn("error creating deferred face err={}", .{err});
            return null;
        };
    }

    fn deferred_face_new_(ptr: [*]const u8, len: usize, presentation: u16) !*DeferredFace {
        const font_str = try alloc.dupeZ(u8, ptr[0..len]);
        errdefer alloc.free(font_str);

        var face: DeferredFace = .{
            .wc = .{
                .alloc = alloc,
                .font_str = font_str,
                .presentation = @enumFromInt(presentation),
            },
        };
        errdefer face.deinit();

        const result = try alloc.create(DeferredFace);
        errdefer alloc.destroy(result);
        result.* = face;
        return result;
    }

    export fn deferred_face_free(ptr: ?*DeferredFace) void {
        if (ptr) |v| {
            v.deinit();
            alloc.destroy(v);
        }
    }

    export fn deferred_face_load(self: *DeferredFace, pts: f32) void {
        self.load(.{}, .{ .points = pts }) catch |err| {
            log.warn("error loading deferred face err={}", .{err});
            return;
        };
    }
};

test "fontconfig" {
    if (options.backend != .fontconfig_freetype) return error.SkipZigTest;

    const discovery = @import("main.zig").discovery;
    const testing = std.testing;
    const alloc = testing.allocator;

    // Load freetype
    var lib = try Library.init(alloc);
    defer lib.deinit();

    // Get a deferred face from fontconfig
    var def = def: {
        var fc = discovery.Fontconfig.init(lib);
        defer fc.deinit();
        var it = try fc.discover(alloc, .{ .family = "monospace", .size = 12 });
        defer it.deinit();
        break :def (try it.next()).?;
    };
    defer def.deinit();

    // Verify we can get the name
    var buf: [1024]u8 = undefined;
    const n = try def.name(&buf);
    try testing.expect(n.len > 0);

    // Load it and verify it works
    var face = try def.load(lib, .{ .size = .{ .points = 12 } });
    defer face.deinit();
    try testing.expect(face.glyphIndex(' ') != null);
}

test "coretext" {
    if (options.backend != .coretext) return error.SkipZigTest;

    const discovery = @import("main.zig").discovery;
    const testing = std.testing;
    const alloc = testing.allocator;

    // Load freetype
    var lib = try Library.init(alloc);
    defer lib.deinit();

    // Get a deferred face from fontconfig
    var def = def: {
        var fc = discovery.CoreText.init(lib);
        var it = try fc.discover(alloc, .{ .family = "Monaco", .size = 12 });
        defer it.deinit();
        break :def (try it.next()).?;
    };
    defer def.deinit();
    try testing.expect(def.hasCodepoint(' ', null));

    // Verify we can get the name
    var buf: [1024]u8 = undefined;
    const n = try def.name(&buf);
    try testing.expect(n.len > 0);

    // Load it and verify it works
    var face = try def.load(lib, .{ .size = .{ .points = 12 } });
    defer face.deinit();
    try testing.expect(face.glyphIndex(' ') != null);
}

test "directwrite" {
    if (options.backend != .directwrite_freetype) return error.SkipZigTest;

    const discovery_mod = @import("main.zig").discovery;
    const testing = std.testing;
    const alloc = testing.allocator;

    var lib = try Library.init(alloc);
    defer lib.deinit();

    var def = def: {
        var dw = discovery_mod.DirectWrite.init();
        defer dw.deinit();
        var it = try dw.discover(alloc, .{ .family = "Consolas", .size = 12 });
        defer it.deinit();
        break :def (try it.next()).?;
    };
    defer def.deinit();

    var buf_dw: [1024]u8 = undefined;
    const n_dw = try def.name(&buf_dw);
    try testing.expect(n_dw.len > 0);

    var face_dw = try def.load(lib, .{ .size = .{ .points = 12 } });
    defer face_dw.deinit();
    try testing.expect(face_dw.glyphIndex(' ') != null);
}
