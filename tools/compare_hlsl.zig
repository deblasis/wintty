const std = @import("std");
const glslpp = @import("glslpp");
const glslang = @import("glslang");
const spvcross = @import("spirv_cross");

pub fn main() !void {
    var gpa: std.heap.GeneralPurposeAllocator(.{}) = .init;
    defer _ = gpa.deinit();
    const alloc = gpa.allocator();

    try glslang.testing.ensureInit();

    const prefix = try std.fs.cwd().readFileAlloc(alloc, "src/renderer/shaders/shadertoy_prefix.glsl", 1024 * 1024);
    defer alloc.free(prefix);
    const crt_body = try std.fs.cwd().readFileAlloc(alloc, "src/renderer/shaders/test_shadertoy_crt.glsl", 1024 * 1024);
    defer alloc.free(crt_body);

    var buf: std.ArrayListUnmanaged(u8) = .{};
    defer buf.deinit(alloc);
    try buf.appendSlice(alloc, prefix);
    try buf.appendSlice(alloc, "\n\n");
    try buf.appendSlice(alloc, crt_body);
    try buf.append(alloc, 0);
    const source: [:0]const u8 = buf.items[0 .. buf.items.len - 1 :0];

    // === Path 1: glslang -> spirv-cross -> HLSL ===
    const c = glslang.c;
    const resource = c.glslang_default_resource();
    const input: c.glslang_input_t = .{
        .language = c.GLSLANG_SOURCE_GLSL,
        .stage = c.GLSLANG_STAGE_FRAGMENT,
        .client = c.GLSLANG_CLIENT_VULKAN,
        .client_version = c.GLSLANG_TARGET_VULKAN_1_2,
        .target_language = c.GLSLANG_TARGET_SPV,
        .target_language_version = c.GLSLANG_TARGET_SPV_1_5,
        .code = source.ptr,
        .default_version = 100,
        .default_profile = c.GLSLANG_NO_PROFILE,
        .force_default_version_and_profile = 0,
        .forward_compatible = 0,
        .messages = c.GLSLANG_MSG_DEFAULT_BIT,
        .resource = resource,
        .callbacks = .{
            .include_system = null,
            .include_local = null,
            .free_include_result = null,
        },
        .callbacks_ctx = null,
    };
    const shader = try glslang.Shader.create(&input);
    defer shader.delete();
    try shader.preprocess(&input);
    try shader.parse(&input);
    const program = try glslang.Program.create();
    defer program.delete();
    program.addShader(shader);
    try program.link(c.GLSLANG_MSG_SPV_RULES_BIT | c.GLSLANG_MSG_VULKAN_RULES_BIT);
    program.spirvGenerate(c.GLSLANG_STAGE_FRAGMENT);
    const spirv_size = program.spirvGetSize();
    const spirv_ptr = try program.spirvGetPtr();
    _ = spirv_size;

    const sc = spvcross.c;
    var ctx: sc.spvc_context = undefined;
    if (sc.spvc_context_create(&ctx) != sc.SPVC_SUCCESS) return error.SpvcFailed;
    defer sc.spvc_context_destroy(ctx);

    var ir: sc.spvc_parsed_ir = undefined;
    if (sc.spvc_context_parse_spirv(ctx, spirv_ptr, program.spirvGetSize(), &ir) != sc.SPVC_SUCCESS) return error.SpvcFailed;

    var compiler: sc.spvc_compiler = undefined;
    if (sc.spvc_context_create_compiler(ctx, sc.SPVC_BACKEND_HLSL, ir, sc.SPVC_CAPTURE_MODE_TAKE_OWNERSHIP, &compiler) != sc.SPVC_SUCCESS) return error.SpvcFailed;

    var options: sc.spvc_compiler_options = undefined;
    if (sc.spvc_compiler_create_compiler_options(compiler, &options) != sc.SPVC_SUCCESS) return error.SpvcFailed;
    if (sc.spvc_compiler_options_set_uint(options, sc.SPVC_COMPILER_OPTION_HLSL_SHADER_MODEL, 60) != sc.SPVC_SUCCESS) return error.SpvcFailed;
    if (sc.spvc_compiler_install_compiler_options(compiler, options) != sc.SPVC_SUCCESS) return error.SpvcFailed;

    var result_ptr: [*:0]const u8 = undefined;
    if (sc.spvc_compiler_compile(compiler, @ptrCast(&result_ptr)) != sc.SPVC_SUCCESS) return error.SpvcFailed;
    const spvc_hlsl = std.mem.sliceTo(result_ptr, 0);

    // === Path 2: glslpp -> HLSL ===
    const glslpp_hlsl = try glslpp.compileGlslToHlsl(alloc, source, .fragment);
    defer alloc.free(glslpp_hlsl);

    // === Save both outputs ===
    const ref_file = try std.fs.cwd().createFile("tools/reference_spirv_cross.hlsl", .{});
    defer ref_file.close();
    try ref_file.writeAll(spvc_hlsl);

    const glslpp_file = try std.fs.cwd().createFile("tools/glslpp_output.hlsl", .{});
    defer glslpp_file.close();
    try glslpp_file.writeAll(glslpp_hlsl);

    // === Comparison ===
    std.debug.print("\n=== SIZE COMPARISON ===\n", .{});
    std.debug.print("spirv-cross: {d} bytes\n", .{spvc_hlsl.len});
    std.debug.print("glslpp:      {d} bytes\n", .{glslpp_hlsl.len});

    std.debug.print("\n=== CBUFFER ===\n", .{});
    printLineContaining(spvc_hlsl, "cbuffer", "spirv-cross");
    printLineContaining(glslpp_hlsl, "cbuffer", "glslpp     ");

    std.debug.print("\n=== TEXTURE ===\n", .{});
    printLineContaining(spvc_hlsl, "register(t", "spirv-cross");
    printLineContaining(glslpp_hlsl, "register(t", "glslpp     ");

    std.debug.print("\n=== SAMPLER ===\n", .{});
    printLineContaining(spvc_hlsl, "SamplerState", "spirv-cross");
    printLineContaining(glslpp_hlsl, "SamplerState", "glslpp     ");

    std.debug.print("\n=== ENTRY POINT ===\n", .{});
    printLineContaining(spvc_hlsl, "SV_Position", "spirv-cross");
    printLineContaining(glslpp_hlsl, "SV_Position", "glslpp     ");
    printLineContaining(spvc_hlsl, "SV_Target", "spirv-cross");
    printLineContaining(glslpp_hlsl, "SV_Target", "glslpp     ");

    std.debug.print("\n=== mainImage SIGNATURE ===\n", .{});
    printLineContaining(spvc_hlsl, "mainImage", "spirv-cross");
    printLineContaining(glslpp_hlsl, "mainImage", "glslpp     ");

    std.debug.print("\n=== .Sample() CALL COUNT ===\n", .{});
    std.debug.print("spirv-cross: {d}\n", .{countOccurances(spvc_hlsl, ".Sample(")});
    std.debug.print("glslpp:      {d}\n", .{countOccurances(glslpp_hlsl, ".Sample(")});

    std.debug.print("\n=== SEMANTICS CHECK ===\n", .{});
    std.debug.print("spirv-cross SV_Position: {}\n", .{std.mem.indexOf(u8, spvc_hlsl, "SV_Position") != null});
    std.debug.print("glslpp      SV_Position: {}\n", .{std.mem.indexOf(u8, glslpp_hlsl, "SV_Position") != null});
    std.debug.print("spirv-cross SV_Target:    {}\n", .{std.mem.indexOf(u8, spvc_hlsl, "SV_Target") != null});
    std.debug.print("glslpp      SV_Target:    {}\n", .{std.mem.indexOf(u8, glslpp_hlsl, "SV_Target") != null});
    std.debug.print("spirv-cross out param:    {}\n", .{std.mem.indexOf(u8, spvc_hlsl, "out ") != null});
    std.debug.print("glslpp      out param:    {}\n", .{std.mem.indexOf(u8, glslpp_hlsl, "out ") != null});

    std.debug.print("\nFiles saved:\n", .{});
    std.debug.print("  tools/reference_spirv_cross.hlsl\n", .{});
    std.debug.print("  tools/glslpp_output.hlsl\n", .{});
}

fn countOccurances(haystack: []const u8, needle: []const u8) usize {
    var count: usize = 0;
    var i: usize = 0;
    while (std.mem.indexOfPos(u8, haystack, i, needle)) |pos| {
        count += 1;
        i = pos + needle.len;
    }
    return count;
}

fn printLineContaining(haystack: []const u8, needle: []const u8, label: []const u8) void {
    var i: usize = 0;
    while (std.mem.indexOfPos(u8, haystack, i, needle)) |pos| {
        const line_start = if (std.mem.lastIndexOf(u8, haystack[0..pos], "\n")) |ls| ls + 1 else 0;
        const line_end = std.mem.indexOfPos(u8, haystack, pos, "\n") orelse haystack.len;
        std.debug.print("{s}: {s}\n", .{ label, std.mem.trim(u8, haystack[line_start..line_end], " \t\r") });
        i = line_end + 1;
    }
}
