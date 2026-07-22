const std = @import("std");
const builtin = @import("builtin");
const Allocator = std.mem.Allocator;
const ArenaAllocator = std.heap.ArenaAllocator;
const zioshade = @import("zioshade");
const configpkg = @import("../config.zig");

const log = std.log.scoped(.shadertoy);

/// The uniform struct used for shadertoy shaders.
pub const Uniforms = extern struct {
    resolution: [3]f32 align(16),
    time: f32 align(4),
    time_delta: f32 align(4),
    frame_rate: f32 align(4),
    frame: i32 align(4),
    channel_time: [4][4]f32 align(16),
    channel_resolution: [4][4]f32 align(16),
    mouse: [4]f32 align(16),
    date: [4]f32 align(16),
    sample_rate: f32 align(4),
    current_cursor: [4]f32 align(16),
    previous_cursor: [4]f32 align(16),
    current_cursor_color: [4]f32 align(16),
    previous_cursor_color: [4]f32 align(16),
    current_cursor_style: i32 align(4),
    previous_cursor_style: i32 align(4),
    cursor_visible: i32 align(4),
    cursor_change_time: f32 align(4),
    time_focus: f32 align(4),
    focus: i32 align(4),
    palette: [256][4]f32 align(16),
    background_color: [4]f32 align(16),
    foreground_color: [4]f32 align(16),
    cursor_color: [4]f32 align(16),
    cursor_text: [4]f32 align(16),
    selection_background_color: [4]f32 align(16),
    selection_foreground_color: [4]f32 align(16),
};

/// The target to load shaders for.
pub const Target = enum { glsl, msl, hlsl };

/// Load a set of shaders from files and convert them to the target
/// format. The shader order is preserved.
pub fn loadFromFiles(
    alloc_gpa: Allocator,
    paths: configpkg.RepeatablePath,
    target: Target,
) ![]const [:0]const u8 {
    var list: std.ArrayList([:0]const u8) = .empty;
    defer list.deinit(alloc_gpa);
    errdefer for (list.items) |shader| alloc_gpa.free(shader);

    for (paths.value.items) |item| {
        const path, const optional = switch (item) {
            .optional => |path| .{ path, true },
            .required => |path| .{ path, false },
        };

        const shader = loadFromFile(alloc_gpa, path, target) catch |err| {
            if (err == error.FileNotFound and optional) {
                continue;
            }

            return err;
        };
        log.info("loaded custom shader path={s}", .{path});
        try list.append(alloc_gpa, shader);
    }

    return try list.toOwnedSlice(alloc_gpa);
}

/// Load a single shader from a file and convert it to the target language
/// using zioshade (pure-Zig GLSL -> SPIR-V -> HLSL/MSL/GLSL compiler).
///
/// On Windows the compile runs on a dedicated large-stack OS thread. The
/// renderer is driven from a .NET/WinUI thread whose default stack is only
/// ~1 MiB, and zioshade's front-end and SPIR-V passes recurse deeply enough
/// on real shaders to overflow it. Compiling on our own thread gives the
/// recursion the headroom it needs.
pub fn loadFromFile(
    alloc_gpa: Allocator,
    path: []const u8,
    target: Target,
) ![:0]const u8 {
    if (builtin.os.tag == .windows) {
        const Ctx = struct {
            alloc_gpa: Allocator,
            path: []const u8,
            target: Target,
            result: anyerror![:0]const u8 = error.Unexpected,

            fn run(self: *@This()) void {
                self.result = compileShader(self.alloc_gpa, self.path, self.target);
            }
        };

        var ctx: Ctx = .{
            .alloc_gpa = alloc_gpa,
            .path = path,
            .target = target,
        };

        const thread = std.Thread.spawn(
            .{ .stack_size = 8 * 1024 * 1024 },
            Ctx.run,
            .{&ctx},
        ) catch |err| return err;
        thread.join();

        return ctx.result;
    }

    return compileShader(alloc_gpa, path, target);
}

/// Compile a shader file through the full GLSL -> SPIR-V -> target pipeline.
fn compileShader(
    alloc_gpa: Allocator,
    path: []const u8,
    target: Target,
) ![:0]const u8 {
    var arena = ArenaAllocator.init(alloc_gpa);
    defer arena.deinit();
    const alloc = arena.allocator();

    // Read it all into memory -- we don't expect shaders to be large.
    const src = src: {
        // Load the shader file
        const cwd = std.fs.cwd();
        const file = try cwd.openFile(path, .{});
        defer file.close();

        break :src try file.readToEndAlloc(
            alloc,
            4 * 1024 * 1024, // 4MB
        );
    };

    // Convert to full GLSL
    const glsl: [:0]const u8 = glsl: {
        var stream: std.Io.Writer.Allocating = .init(alloc);
        try glslFromShader(&stream.writer, src);
        try stream.writer.writeByte(0);
        break :glsl stream.written()[0 .. stream.written().len - 1 :0];
    };

    // All targets go through zioshade's one-shot compilers. The HLSL path
    // applies binding_shift -1 so the Globals cbuffer lands in register(b0)
    // with its channel texture/sampler in t0/s0; the MSL path emits fragment
    // entry main0 with the uniform buffer at [[buffer(1)]]; the GLSL path
    // emits #version 430. These are the contracts the renderers depend on.
    return switch (target) {
        .hlsl => zioshade.compileGlslToHlsl(alloc_gpa, glsl, .fragment) catch |err| {
            log.warn("zioshade HLSL compile failed path={s} err={}", .{ path, err });
            logCompileDiagnostics(alloc, glsl, path);
            return err;
        },
        .msl => zioshade.compileGlslToMsl(alloc_gpa, glsl, .fragment) catch |err| {
            log.warn("zioshade MSL compile failed path={s} err={}", .{ path, err });
            logCompileDiagnostics(alloc, glsl, path);
            return err;
        },
        .glsl => zioshade.compileGlslToGlsl(alloc_gpa, glsl, .fragment) catch |err| {
            log.warn("zioshade GLSL compile failed path={s} err={}", .{ path, err });
            logCompileDiagnostics(alloc, glsl, path);
            return err;
        },
    };
}

/// Re-run the GLSL front-end with structured diagnostics so a failed
/// compile logs line/column details instead of only an error name. Backend
/// (SPIR-V to target) failures produce no front-end diagnostics, in which
/// case this logs nothing extra. `alloc` is expected to be an arena; the
/// diagnostic messages are freed with it.
fn logCompileDiagnostics(alloc: Allocator, glsl: [:0]const u8, path: []const u8) void {
    var diags: std.ArrayListUnmanaged(zioshade.diagnostic.Diagnostic) = .empty;
    _ = zioshade.compileToSPIRVWithDiagnostics(alloc, glsl, .{
        .stage = .fragment,
        .version = 430,
    }, &diags) catch {};
    for (diags.items) |d| {
        log.warn("shader {s} path={s} line={d} column={d}: {s}", .{
            @tagName(d.kind),
            path,
            d.line,
            d.column,
            d.message,
        });
    }
}

/// Convert a ShaderToy shader into valid GLSL.
///
/// ShaderToy shaders aren't full shaders, they're just implementing a
/// mainImage function and don't define any of the uniforms. This function
/// will convert the ShaderToy shader into a valid GLSL shader that can be
/// compiled and linked.
pub fn glslFromShader(writer: *std.Io.Writer, src: []const u8) !void {
    const prefix = @embedFile("shaders/shadertoy_prefix.glsl");
    try writer.writeAll(prefix);
    try writer.writeAll("\n\n");
    try writer.writeAll(src);
}

/// Convert ShaderToy shader to null-terminated glsl for testing.
fn testGlslZ(alloc: Allocator, src: []const u8) ![:0]const u8 {
    var buf: std.Io.Writer.Allocating = .init(alloc);
    defer buf.deinit();
    try glslFromShader(&buf.writer, src);
    return try buf.toOwnedSliceSentinel(0);
}

test "zioshade compiles CRT shader to HLSL" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const src = try testGlslZ(alloc, test_crt);
    defer alloc.free(src);

    const hlsl = try zioshade.compileGlslToHlsl(alloc, src, .fragment);
    defer alloc.free(hlsl);
    try testing.expect(hlsl.len > 0);

    // Golden invariants the DX12 renderer depends on: the Globals uniform
    // block must land in register(b0) (binding_shift -1), the channel
    // texture in t0 with its sampler in s0, and the pixel shader must
    // return SV_Target.
    try testing.expect(std.mem.indexOf(u8, hlsl, "register(b0)") != null);
    try testing.expect(std.mem.indexOf(u8, hlsl, "register(t0)") != null);
    try testing.expect(std.mem.indexOf(u8, hlsl, "register(s0)") != null);
    try testing.expect(std.mem.indexOf(u8, hlsl, "SV_Target") != null);
}

test "zioshade compiles CRT shader to MSL" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const src = try testGlslZ(alloc, test_crt);
    defer alloc.free(src);

    const msl = try zioshade.compileGlslToMsl(alloc, src, .fragment);
    defer alloc.free(msl);
    try testing.expect(msl.len > 0);

    // Golden invariants the Metal renderer depends on: fragment entry point
    // main0 and the uniform buffer at [[buffer(1)]].
    try testing.expect(std.mem.indexOf(u8, msl, "main0") != null);
    try testing.expect(std.mem.indexOf(u8, msl, "[[buffer(1)]]") != null);
}

test "zioshade compiles CRT shader to GLSL" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const src = try testGlslZ(alloc, test_crt);
    defer alloc.free(src);

    const glsl = try zioshade.compileGlslToGlsl(alloc, src, .fragment);
    defer alloc.free(glsl);
    try testing.expect(glsl.len > 0);

    // Golden invariant the OpenGL renderer depends on: modern GLSL output.
    try testing.expect(std.mem.startsWith(u8, glsl, "#version 430"));
}

test "zioshade compiles focus shader to all targets" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const src = try testGlslZ(alloc, test_focus);
    defer alloc.free(src);

    const hlsl = try zioshade.compileGlslToHlsl(alloc, src, .fragment);
    defer alloc.free(hlsl);
    try testing.expect(hlsl.len > 0);
    try testing.expect(std.mem.indexOf(u8, hlsl, "register(b0)") != null);
    try testing.expect(std.mem.indexOf(u8, hlsl, "SV_Target") != null);

    const msl = try zioshade.compileGlslToMsl(alloc, src, .fragment);
    defer alloc.free(msl);
    try testing.expect(msl.len > 0);
    try testing.expect(std.mem.indexOf(u8, msl, "main0") != null);
    try testing.expect(std.mem.indexOf(u8, msl, "[[buffer(1)]]") != null);

    const glsl = try zioshade.compileGlslToGlsl(alloc, src, .fragment);
    defer alloc.free(glsl);
    try testing.expect(glsl.len > 0);
    try testing.expect(std.mem.startsWith(u8, glsl, "#version 430"));
}

test "zioshade loadFromFile compiles a real shader file from disk to HLSL" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // Exercise the exact runtime entry point the renderer calls (including
    // the Windows large-stack worker thread): write a shader to disk, then
    // read it back, prepend the shadertoy prefix, and compile to the DX12
    // target through zioshade. Uses a temp dir so we don't depend on cwd.
    var tmp = testing.tmpDir(.{});
    defer tmp.cleanup();
    try tmp.dir.writeFile(.{ .sub_path = "shader.glsl", .data = test_crt });

    var path_buf: [std.fs.max_path_bytes]u8 = undefined;
    const path = try tmp.dir.realpath("shader.glsl", &path_buf);

    const hlsl = try loadFromFile(alloc, path, .hlsl);
    defer alloc.free(hlsl);

    try testing.expect(std.mem.indexOf(u8, hlsl, "register(b0)") != null);
    try testing.expect(std.mem.indexOf(u8, hlsl, "register(t0)") != null);
    try testing.expect(std.mem.indexOf(u8, hlsl, "register(s0)") != null);
    try testing.expect(std.mem.indexOf(u8, hlsl, "SV_Target") != null);
}

test "zioshade rejects invalid shader" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const src = try testGlslZ(alloc, test_invalid);
    defer alloc.free(src);

    // zioshade's error taxonomy is not stable pre-1.0, so assert only that
    // the invalid shader is rejected rather than matching a specific error.
    if (zioshade.compileGlslToHlsl(alloc, src, .fragment)) |hlsl| {
        alloc.free(hlsl);
        return error.TestUnexpectedResult;
    } else |_| {}
}

const test_crt = @embedFile("shaders/test_shadertoy_crt.glsl");
const test_invalid = @embedFile("shaders/test_shadertoy_invalid.glsl");
const test_focus = @embedFile("shaders/test_shadertoy_focus.glsl");
