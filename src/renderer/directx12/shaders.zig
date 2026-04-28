//! Shader management for DX12.
//!
//! Loads DXIL bytecode via @embedFile and defines input element descriptions
//! for all 5 pipelines. GPU data structs are imported from gpu_data.zig.
const std = @import("std");
const builtin = @import("builtin");
const com = @import("com.zig");

const d3d12 = @import("d3d12.zig");
const gpu_data = @import("gpu_data.zig");
const Pipeline = @import("Pipeline.zig");

const log = std.log.scoped(.dx12_shaders);

pub const Uniforms = gpu_data.Uniforms;
pub const CellText = gpu_data.CellText;
pub const CellBg = gpu_data.CellBg;
pub const Image = gpu_data.Image;
pub const BgImage = gpu_data.BgImage;

/// Embedded shader bytecode -- compiled at build time by HlslStep.zig.
/// On non-Windows these are empty slices so the module still compiles.
const shader_bytecode = if (builtin.os.tag == .windows) struct {
    const bg_color_vs = @embedFile("ghostty_hlsl_bg_color_vs");
    const bg_color_ps = @embedFile("ghostty_hlsl_bg_color_ps");
    const cell_bg_ps = @embedFile("ghostty_hlsl_cell_bg_ps");
    const cell_text_vs = @embedFile("ghostty_hlsl_cell_text_vs");
    const cell_text_ps = @embedFile("ghostty_hlsl_cell_text_ps");
    const image_vs = @embedFile("ghostty_hlsl_image_vs");
    const image_ps = @embedFile("ghostty_hlsl_image_ps");
    const bg_image_vs = @embedFile("ghostty_hlsl_bg_image_vs");
    const bg_image_ps = @embedFile("ghostty_hlsl_bg_image_ps");
} else struct {
    const bg_color_vs: []const u8 = &.{};
    const bg_color_ps: []const u8 = &.{};
    const cell_bg_ps: []const u8 = &.{};
    const cell_text_vs: []const u8 = &.{};
    const cell_text_ps: []const u8 = &.{};
    const image_vs: []const u8 = &.{};
    const image_ps: []const u8 = &.{};
    const bg_image_vs: []const u8 = &.{};
    const bg_image_ps: []const u8 = &.{};
};

/// Compile HLSL source to DXIL bytecode using DXC.
/// Returns null on failure. Caller owns returned memory.
fn compileHlslToDxil(alloc: std.mem.Allocator, source: [:0]const u8, entry_point: [*:0]const u16) error{OutOfMemory}!?[]const u8 {
    const dxc_lib = d3d12.DxcLibrary.load() orelse {
        log.warn("dxcompiler.dll not found, cannot compile custom shader", .{});
        return null;
    };
    defer dxc_lib.deinit();

    // Create DXC utils
    var utils_raw: ?*anyopaque = null;
    if (com.FAILED(dxc_lib.createInstance(&d3d12.CLSID_DxcUtils, &d3d12.IDxcUtils.IID, &utils_raw))) {
        log.warn("DXC utils creation failed", .{});
        return null;
    }
    const utils: *d3d12.IDxcUtils = @ptrCast(@alignCast(utils_raw));
    defer _ = utils.Release();

    // Create compiler
    var compiler_raw: ?*anyopaque = null;
    if (com.FAILED(dxc_lib.createInstance(&d3d12.CLSID_DxcCompiler, &d3d12.IDxcCompiler3.IID, &compiler_raw))) {
        log.warn("DXC compiler creation failed", .{});
        return null;
    }
    const compiler: *d3d12.IDxcCompiler3 = @ptrCast(@alignCast(compiler_raw));
    defer _ = compiler.Release();

    // Create include handler
    var include_handler_raw: ?*anyopaque = null;
    if (com.FAILED(utils.CreateDefaultIncludeHandler(&include_handler_raw))) {
        log.warn("DXC include handler creation failed", .{});
        return null;
    }
    const include_handler: *anyopaque = @ptrCast(@alignCast(include_handler_raw));
    defer {
        // Release as IUnknown interface since it's not used after compilation
        const include_handler_iface: *com.IUnknown = @ptrCast(@alignCast(include_handler_raw));
        _ = include_handler_iface.Release();
    }

    // Prepare source buffer
    const source_buffer = d3d12.DxcBuffer{
        .Ptr = source.ptr,
        .Size = source.len,
        .Encoding = 0, // UTF-8
    };

    // Build compilation arguments: -T ps_6_0 -E <entry> -O3 -Zpc
    const target_flag = std.unicode.utf8ToUtf16LeStringLiteral("-T");
    const target_profile = std.unicode.utf8ToUtf16LeStringLiteral("ps_6_0");
    const entry_flag = std.unicode.utf8ToUtf16LeStringLiteral("-E");
    const opt_level = std.unicode.utf8ToUtf16LeStringLiteral("-O3");
    const packing = std.unicode.utf8ToUtf16LeStringLiteral("-Zpc");

    const args = [_]?[*:0]const u16{
        target_flag,
        target_profile,
        entry_flag,
        entry_point,
        opt_level,
        packing,
    };

    // Compile
    var result_raw: ?*anyopaque = null;
    const hr = compiler.Compile(
        &source_buffer,
        &args,
        args.len,
        include_handler,
        &d3d12.IDxcResult.IID,
        &result_raw,
    );

    if (com.FAILED(hr)) {
        log.warn("DXC Compile() failed, hr=0x{x}", .{hr});
        return null;
    }

    const result: *d3d12.IDxcResult = @ptrCast(@alignCast(result_raw));
    defer _ = result.Release();

    // Check compilation status
    const status = result.GetStatus();
    if (com.FAILED(status)) {
        log.warn("DXC compile failed, status=0x{x}", .{status});
        logCompileErrors(result);
        return null;
    }

    // Extract DXIL bytecode
    var bytecode_raw: ?*anyopaque = null;
    var output_object: ?*anyopaque = null;
    // DXIL output is IDxcBlob, not IDxcBlobUtf8
    const get_output_hr = result.GetOutput(d3d12.DXC_OUT_KIND.OBJECT, &d3d12.IDxcBlob.IID, &bytecode_raw, &output_object);
    if (com.FAILED(get_output_hr)) {
        log.warn("DXC GetOutput(OBJECT) failed, hr=0x{x}", .{get_output_hr});
        return null;
    }

    // output_object is a secondary COM reference (IDxcBlobWide with the
    // output filename) that must be released to avoid leaking on every
    // successful compilation.
    if (output_object) |oo| {
        const wide: *d3d12.IDxcBlobUtf8 = @ptrCast(@alignCast(oo));
        _ = wide.Release();
    }

    const bytecode: *d3d12.IDxcBlobUtf8 = @ptrCast(@alignCast(bytecode_raw));
    defer _ = bytecode.Release();

    // Copy into allocator-owned slice
    const ptr = bytecode.GetBufferPointer();
    const size = bytecode.GetBufferSize();
    const slice = try alloc.alloc(u8, size);
    const src_ptr: [*]const u8 = @ptrCast(ptr);
    @memcpy(slice, src_ptr);
    return slice;
}

/// Log compilation errors from IDxcResult.
fn logCompileErrors(result: *d3d12.IDxcResult) void {
    var error_blob_raw: ?*anyopaque = null;
    var error_output: ?*anyopaque = null;
    if (com.FAILED(result.GetOutput(d3d12.DXC_OUT_KIND.ERRORS, &d3d12.IDxcBlobUtf8.IID, &error_blob_raw, &error_output))) {
        return;
    }

    if (error_blob_raw) |raw| {
        const error_blob: *d3d12.IDxcBlobUtf8 = @ptrCast(@alignCast(raw));
        defer _ = error_blob.Release();

        if (error_output) |eo| {
            const wide: *d3d12.IDxcBlobUtf8 = @ptrCast(@alignCast(eo));
            _ = wide.Release();
        }

        const error_str = error_blob.GetStringPointer();
        const error_slice = std.mem.sliceTo(error_str, 0);
        if (error_slice.len > 0) {
            log.warn("DXC errors: {s}", .{error_slice});
        }
    }
}

// --- Input element descriptions for instanced pipelines ---
const PER_INSTANCE = d3d12.D3D12_INPUT_CLASSIFICATION.PER_INSTANCE_DATA;

/// Input layout for CellText instances (32 bytes per instance).
const cell_text_input_elements = [_]d3d12.D3D12_INPUT_ELEMENT_DESC{
    .{ // glyph_pos: [2]u32
        .SemanticName = "GLYPH_POS",
        .SemanticIndex = 0,
        .Format = .R32G32_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 0,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // glyph_size: [2]u32
        .SemanticName = "GLYPH_SIZE",
        .SemanticIndex = 0,
        .Format = .R32G32_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 8,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // bearings: [2]i16
        .SemanticName = "BEARINGS",
        .SemanticIndex = 0,
        .Format = .R16G16_SINT,
        .InputSlot = 0,
        .AlignedByteOffset = 16,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // grid_pos: [2]u16
        .SemanticName = "GRID_POS",
        .SemanticIndex = 0,
        .Format = .R16G16_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 20,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // color: [4]u8
        .SemanticName = "COLOR",
        .SemanticIndex = 0,
        .Format = .R8G8B8A8_UNORM,
        .InputSlot = 0,
        .AlignedByteOffset = 24,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // atlas: Atlas (u8)
        .SemanticName = "ATLAS",
        .SemanticIndex = 0,
        .Format = .R8_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 28,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // bools: packed struct(u8)
        .SemanticName = "BOOLS",
        .SemanticIndex = 0,
        .Format = .R8_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 29,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
};

/// Input layout for Image instances (40 bytes per instance).
const image_input_elements = [_]d3d12.D3D12_INPUT_ELEMENT_DESC{
    .{ // grid_pos: [2]f32
        .SemanticName = "GRID_POS",
        .SemanticIndex = 0,
        .Format = .R32G32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 0,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // cell_offset: [2]f32
        .SemanticName = "CELL_OFFSET",
        .SemanticIndex = 0,
        .Format = .R32G32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 8,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // source_rect: [4]f32
        .SemanticName = "SOURCE_RECT",
        .SemanticIndex = 0,
        .Format = .R32G32B32A32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 16,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // dest_size: [2]f32
        .SemanticName = "DEST_SIZE",
        .SemanticIndex = 0,
        .Format = .R32G32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 32,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
};

/// Input layout for BgImage instances (8 bytes per instance).
const bg_image_input_elements = [_]d3d12.D3D12_INPUT_ELEMENT_DESC{
    .{ // opacity: f32
        .SemanticName = "OPACITY",
        .SemanticIndex = 0,
        .Format = .R32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 0,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // info: packed struct(u8)
        .SemanticName = "INFO",
        .SemanticIndex = 0,
        .Format = .R8_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 4,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
};

// Cross-reference input layout byte offsets against GPU data struct offsets.
// gpu_data.zig already asserts struct sizes; these verify the input element
// descriptions stay in sync with the struct layout.
comptime {
    // CellText: last element (bools) must match struct offset
    std.debug.assert(cell_text_input_elements[0].AlignedByteOffset == @offsetOf(CellText, "glyph_pos"));
    std.debug.assert(cell_text_input_elements[1].AlignedByteOffset == @offsetOf(CellText, "glyph_size"));
    std.debug.assert(cell_text_input_elements[2].AlignedByteOffset == @offsetOf(CellText, "bearings"));
    std.debug.assert(cell_text_input_elements[3].AlignedByteOffset == @offsetOf(CellText, "grid_pos"));
    std.debug.assert(cell_text_input_elements[4].AlignedByteOffset == @offsetOf(CellText, "color"));
    std.debug.assert(cell_text_input_elements[5].AlignedByteOffset == @offsetOf(CellText, "atlas"));
    std.debug.assert(cell_text_input_elements[6].AlignedByteOffset == @offsetOf(CellText, "bools"));
    // Image: last element (dest_size) must match struct offset
    std.debug.assert(image_input_elements[0].AlignedByteOffset == @offsetOf(Image, "grid_pos"));
    std.debug.assert(image_input_elements[1].AlignedByteOffset == @offsetOf(Image, "cell_offset"));
    std.debug.assert(image_input_elements[2].AlignedByteOffset == @offsetOf(Image, "source_rect"));
    std.debug.assert(image_input_elements[3].AlignedByteOffset == @offsetOf(Image, "dest_size"));
    // BgImage: info element must match struct offset
    std.debug.assert(bg_image_input_elements[0].AlignedByteOffset == @offsetOf(BgImage, "opacity"));
    std.debug.assert(bg_image_input_elements[1].AlignedByteOffset == @offsetOf(BgImage, "info"));
}

pub const Shaders = struct {
    /// Shared root signature owned by this struct. Pipelines reference it
    /// for draw-time binding but do not own it -- deinit releases it here.
    root_signature: ?*d3d12.ID3D12RootSignature = null,
    /// Separate root signature for custom post-process shaders. Has exactly
    /// the bindings the SPIRV-Cross HLSL output expects (b0, t0, s0) without
    /// the main root signature's extra SRV slots.
    post_root_signature: ?*d3d12.ID3D12RootSignature = null,
    pipelines: Pipelines = .{},
    post_pipelines: []const Pipeline = &.{},
    defunct: bool = false,

    pub const Pipelines = struct {
        bg_color: Pipeline = .{},
        cell_bg: Pipeline = .{},
        cell_text: Pipeline = .{},
        image: Pipeline = .{},
        bg_image: Pipeline = .{},
    };

    pub const InitError = error{
        RootSignatureSerializeFailed,
        RootSignatureCreationFailed,
        PipelineStateCreationFailed,
        OutOfMemory,
    };

    /// Release all PSOs that have been created so far.
    /// Used as errdefer cleanup during init.
    fn releasePipelines(pipelines: *Pipelines) void {
        inline for (@typeInfo(Pipelines).@"struct".fields) |field| {
            @field(pipelines, field.name).deinit();
        }
    }

    /// Initialize all 5 pipelines from embedded DXIL bytecode.
    /// On non-Windows, returns default-initialized (empty) pipelines.
    pub fn init(device: ?*d3d12.ID3D12Device, alloc: std.mem.Allocator, custom_shaders: []const [:0]const u8) InitError!Shaders {
        const dev = device orelse return .{};

        // All pipelines share a single root signature.
        const root_sig = Pipeline.createRootSignature(dev) catch |err| return err;
        errdefer _ = root_sig.Release();

        var pipelines: Pipelines = .{};
        errdefer releasePipelines(&pipelines);

        pipelines.bg_color = try Pipeline.init(.{
            .device = dev,
            .root_signature = root_sig,
            .vs_bytecode = shader_bytecode.bg_color_vs,
            .ps_bytecode = shader_bytecode.bg_color_ps,
            .blend = .premultiplied_alpha,
        });
        pipelines.cell_bg = try Pipeline.init(.{
            .device = dev,
            .root_signature = root_sig,
            .vs_bytecode = shader_bytecode.bg_color_vs,
            .ps_bytecode = shader_bytecode.cell_bg_ps,
            .blend = .premultiplied_alpha,
        });
        pipelines.cell_text = try Pipeline.init(.{
            .device = dev,
            .root_signature = root_sig,
            .vs_bytecode = shader_bytecode.cell_text_vs,
            .ps_bytecode = shader_bytecode.cell_text_ps,
            .input_layout = &cell_text_input_elements,
            .blend = .premultiplied_alpha,
        });
        pipelines.image = try Pipeline.init(.{
            .device = dev,
            .root_signature = root_sig,
            .vs_bytecode = shader_bytecode.image_vs,
            .ps_bytecode = shader_bytecode.image_ps,
            .input_layout = &image_input_elements,
            .blend = .premultiplied_alpha,
        });
        pipelines.bg_image = try Pipeline.init(.{
            .device = dev,
            .root_signature = root_sig,
            .vs_bytecode = shader_bytecode.bg_image_vs,
            .ps_bytecode = shader_bytecode.bg_image_ps,
            .input_layout = &bg_image_input_elements,
            .blend = .premultiplied_alpha,
        });

        // Compile custom post-process shaders
        var post_list = std.ArrayListUnmanaged(Pipeline){};
        errdefer {
            for (post_list.items) |*p| p.deinit();
            post_list.deinit(alloc);
        }

        // Create a dedicated root signature for post-process shaders.
        // Uses CBV at b0 (binding remapped in shader_wrapper before glslang),
        // 1 SRV at t0, 1 sampler at s0.
        const post_root_sig = if (custom_shaders.len > 0)
            Pipeline.createPostRootSignature(dev) catch null
        else
            null;
        errdefer {
            if (post_root_sig) |rs| {
                _ = rs.Release();
            }
        }

        if (custom_shaders.len == 0 or post_root_sig == null) {
            return .{
                .root_signature = root_sig,
                .pipelines = pipelines,
            };
        }

        const entry_point_utf8 = "main";
        const entry_point_w = std.unicode.utf8ToUtf16LeAllocZ(std.heap.c_allocator, entry_point_utf8) catch |err| {
            log.warn("UTF-16 conversion failed for entry point: {}", .{err});
            // Return with main pipelines intact; post_pipelines remains empty.
            return .{
                .root_signature = root_sig,
                .post_root_signature = post_root_sig,
                .pipelines = pipelines,
                .post_pipelines = &.{},
            };
        };
        defer std.heap.c_allocator.free(entry_point_w);

        const custom_root_sig = post_root_sig orelse root_sig;

        for (custom_shaders) |source| {
            const dxil = compileHlslToDxil(alloc, source, entry_point_w) catch continue orelse continue;
            // D3D12 copies bytecode into the PSO during CreateGraphicsPipelineState,
            // so the allocation can be freed immediately after PSO creation.
            defer alloc.free(dxil);

            const pso = Pipeline.init(.{
                .device = dev,
                .root_signature = custom_root_sig,
                .vs_bytecode = shader_bytecode.bg_color_vs,
                .ps_bytecode = dxil,
                .blend = .none,
            }) catch |err| {
                log.warn("custom shader PSO creation failed: {}", .{err});
                continue;
            };

            try post_list.append(alloc, pso);
        }

        const post_pipelines = try post_list.toOwnedSlice(alloc);

        return .{
            .root_signature = root_sig,
            .post_root_signature = post_root_sig,
            .pipelines = pipelines,
            .post_pipelines = post_pipelines,
        };
    }

    pub fn deinit(self: *Shaders, alloc: std.mem.Allocator) void {
        for (self.post_pipelines) |p| {
            p.deinit();
        }
        if (self.post_pipelines.len > 0) {
            alloc.free(self.post_pipelines);
        }
        self.post_pipelines = &.{};

        inline for (@typeInfo(Pipelines).@"struct".fields) |field| {
            @field(self.pipelines, field.name).deinit();
        }
        self.pipelines = .{};

        if (self.root_signature) |rs| _ = rs.Release();
        self.root_signature = null;
        if (self.post_root_signature) |rs| _ = rs.Release();
        self.post_root_signature = null;

        self.* = undefined;
    }
};

// --- Tests ---

test "shader bytecode fields exist" {
    // Verify all expected bytecode fields are present.
    _ = shader_bytecode.bg_color_vs;
    _ = shader_bytecode.bg_color_ps;
    _ = shader_bytecode.cell_bg_ps;
    _ = shader_bytecode.cell_text_vs;
    _ = shader_bytecode.cell_text_ps;
    _ = shader_bytecode.image_vs;
    _ = shader_bytecode.image_ps;
    _ = shader_bytecode.bg_image_vs;
    _ = shader_bytecode.bg_image_ps;
}

test "cell_text_input_elements count" {
    try std.testing.expectEqual(@as(usize, 7), cell_text_input_elements.len);
}

test "image_input_elements count" {
    try std.testing.expectEqual(@as(usize, 4), image_input_elements.len);
}

test "bg_image_input_elements count" {
    try std.testing.expectEqual(@as(usize, 2), bg_image_input_elements.len);
}

test "Shaders struct fields" {
    try std.testing.expect(@hasField(Shaders, "root_signature"));
    try std.testing.expect(@hasField(Shaders, "pipelines"));
    try std.testing.expect(@hasField(Shaders, "post_pipelines"));
    try std.testing.expect(@hasField(Shaders, "defunct"));
}

test "Shaders default is empty" {
    const s: Shaders = .{};
    try std.testing.expect(s.root_signature == null);
    try std.testing.expect(s.pipelines.bg_color.pso == null);
    try std.testing.expect(s.pipelines.cell_text.pso == null);
    try std.testing.expect(s.post_pipelines.len == 0);
    try std.testing.expect(!s.defunct);
}

test "Shaders.init returns default on null device" {
    const s = try Shaders.init(null, std.testing.allocator, &.{});
    try std.testing.expect(s.root_signature == null);
    try std.testing.expect(s.pipelines.bg_color.pso == null);
    try std.testing.expect(s.pipelines.cell_text.pso == null);
}

test "Pipelines has all 5 fields" {
    try std.testing.expect(@hasField(Shaders.Pipelines, "bg_color"));
    try std.testing.expect(@hasField(Shaders.Pipelines, "cell_bg"));
    try std.testing.expect(@hasField(Shaders.Pipelines, "cell_text"));
    try std.testing.expect(@hasField(Shaders.Pipelines, "image"));
    try std.testing.expect(@hasField(Shaders.Pipelines, "bg_image"));
}

test "pipeline deinit loop does not touch root_signature" {
    // Shaders.deinit releases root_signature separately, after calling
    // deinit on each pipeline. If Pipeline.deinit ever started releasing
    // root_signature, the second release in Shaders.deinit would
    // double-free. This test catches that by setting a bogus
    // root_signature that would crash if dereferenced.
    const sentinel: *d3d12.ID3D12RootSignature = @ptrFromInt(0xDEAD_BEF0);
    var pipelines: Shaders.Pipelines = .{};

    inline for (@typeInfo(Shaders.Pipelines).@"struct".fields) |field| {
        @field(pipelines, field.name).root_signature = sentinel;
    }

    // Same loop as Shaders.deinit -- must not dereference root_signature.
    inline for (@typeInfo(Shaders.Pipelines).@"struct".fields) |field| {
        @field(pipelines, field.name).deinit();
    }
}

test "all input elements use PER_INSTANCE_DATA" {
    for (&cell_text_input_elements) |elem| {
        try std.testing.expectEqual(PER_INSTANCE, elem.InputSlotClass);
    }
    for (&image_input_elements) |elem| {
        try std.testing.expectEqual(PER_INSTANCE, elem.InputSlotClass);
    }
    for (&bg_image_input_elements) |elem| {
        try std.testing.expectEqual(PER_INSTANCE, elem.InputSlotClass);
    }
}
