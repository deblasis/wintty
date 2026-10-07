//! Shader management for DX12.
//!
//! Loads DXIL bytecode via @embedFile and defines input element descriptions
//! for all 5 pipelines. GPU data structs are imported from gpu_data.zig.
const std = @import("std");
const builtin = @import("builtin");
const com = @import("com.zig");

const d3d12 = @import("d3d12.zig");
const renderer = @import("../../renderer.zig");
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

/// Compile HLSL source to DXIL bytecode using DXC (pixel profile).
/// Returns null on failure. Caller owns returned memory.
pub fn compileHlslToDxil(alloc: std.mem.Allocator, source: [:0]const u8, entry_point: [*:0]const u16) error{OutOfMemory}!?[]const u8 {
    return compileHlslToDxilProfile(alloc, source, entry_point, std.unicode.utf8ToUtf16LeStringLiteral("ps_6_0"));
}

/// Compile HLSL source to DXIL bytecode using DXC (explicit profile).
/// Returns null on failure. Caller owns returned memory.
pub fn compileHlslToDxilProfile(
    alloc: std.mem.Allocator,
    source: [:0]const u8,
    entry_point: [*:0]const u16,
    profile: [*:0]const u16,
) error{OutOfMemory}!?[]const u8 {
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

    // Build compilation arguments: -T <profile> -E <entry> -O3 -Zpc
    const target_flag = std.unicode.utf8ToUtf16LeStringLiteral("-T");
    const entry_flag = std.unicode.utf8ToUtf16LeStringLiteral("-E");
    const opt_level = std.unicode.utf8ToUtf16LeStringLiteral("-O3");
    const packing = std.unicode.utf8ToUtf16LeStringLiteral("-Zpc");

    const args = [_]?[*:0]const u16{
        target_flag,
        profile,
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
    /// A set that holds nothing. The generic renderer starts every backend
    /// here (shaders are built lazily on the render thread), and `deinit`
    /// leaves one behind, so a failure between deiniting a set and building
    /// the next leaves something safe to deinit again rather than
    /// `undefined`. Defunct, so a draw that reaches it is caught by the same
    /// assert a failed reinit always tripped, and `deinit` on it is a no-op.
    pub const uninit: Shaders = .{ .defunct = true };

    /// The same placeholder under the name the device-recovery path uses.
    pub const empty: Shaders = uninit;

    /// Shared root signature owned by this struct. Pipelines reference it
    /// for draw-time binding but do not own it -- deinit releases it here.
    root_signature: ?*d3d12.ID3D12RootSignature = null,
    /// Separate root signature for custom post-process shaders. Has exactly
    /// the bindings the SPIRV-Cross HLSL output expects (b0, t0, s0) without
    /// the main root signature's extra SRV slots.
    post_root_signature: ?*d3d12.ID3D12RootSignature = null,
    pipelines: Pipelines = .{},
    post_pipelines: []const Pipeline = &.{},
    /// Why `post_pipelines` is empty despite shaders having been requested.
    /// Null when none were requested, or when at least one built.
    ///
    /// An empty slice on its own cannot distinguish "the user configured no
    /// shaders" from "every shader the user configured failed to build",
    /// which is exactly what made this failure silent. The generic renderer
    /// reads this to raise a user-facing notice.
    post_failure: ?renderer.CustomShaderFailure = null,
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
            .texture_tables = &Pipeline.main_texture_tables,
            .vs_bytecode = shader_bytecode.bg_color_vs,
            .ps_bytecode = shader_bytecode.bg_color_ps,
            .blend = .premultiplied_alpha,
        });
        pipelines.cell_bg = try Pipeline.init(.{
            .device = dev,
            .root_signature = root_sig,
            .texture_tables = &Pipeline.main_texture_tables,
            .vs_bytecode = shader_bytecode.bg_color_vs,
            .ps_bytecode = shader_bytecode.cell_bg_ps,
            .blend = .premultiplied_alpha,
        });
        pipelines.cell_text = try Pipeline.init(.{
            .device = dev,
            .root_signature = root_sig,
            .texture_tables = &Pipeline.main_texture_tables,
            .vs_bytecode = shader_bytecode.cell_text_vs,
            .ps_bytecode = shader_bytecode.cell_text_ps,
            .input_layout = &cell_text_input_elements,
            .blend = .premultiplied_alpha,
        });
        pipelines.image = try Pipeline.init(.{
            .device = dev,
            .root_signature = root_sig,
            .texture_tables = &Pipeline.main_texture_tables,
            .vs_bytecode = shader_bytecode.image_vs,
            .ps_bytecode = shader_bytecode.image_ps,
            .input_layout = &image_input_elements,
            .blend = .premultiplied_alpha,
        });
        pipelines.bg_image = try Pipeline.init(.{
            .device = dev,
            .root_signature = root_sig,
            .texture_tables = &Pipeline.main_texture_tables,
            .vs_bytecode = shader_bytecode.bg_image_vs,
            .ps_bytecode = shader_bytecode.bg_image_ps,
            .input_layout = &bg_image_input_elements,
            .blend = .premultiplied_alpha,
        });

        // Compile custom post-process shaders
        var post_list: std.ArrayListUnmanaged(Pipeline) = .empty;
        errdefer {
            for (post_list.items) |*p| p.deinit();
            post_list.deinit(alloc);
        }

        // Create a dedicated root signature for post-process shaders.
        // Uses CBV at b0 (binding shifted to b0 by zioshade), 1 SRV at t0,
        // 1 sampler at s0.
        const post_root_sig = if (custom_shaders.len > 0)
            Pipeline.createPostRootSignature(dev) catch |err| root_sig: {
                // Every other custom-shader failure path logs its reason.
                // Without this one the user sees pipeline_failed with no
                // explanation anywhere.
                log.warn("custom shader post root signature failed: {}", .{err});
                break :root_sig null;
            }
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
                // Reaching here with shaders requested means the post root
                // signature failed to serialize, which is a pipeline-object
                // failure rather than anything to do with the shader source.
                .post_failure = if (custom_shaders.len > 0) .pipeline_failed else null,
            };
        }

        // Probe for the compiler once, up front. `compileHlslToDxil` loads
        // the DLL itself per shader, but the reason the post pipelines come
        // out empty has to be distinguishable: "this build cannot compile
        // shaders at all" is a packaging defect we own, while "your shader
        // did not compile" is something the user can fix. Both produce an
        // identical empty slice.
        if (d3d12.DxcLibrary.load()) |probe| {
            probe.deinit();
        } else {
            log.warn("dxcompiler.dll not found, cannot compile custom shader", .{});
            return .{
                .root_signature = root_sig,
                .post_root_signature = post_root_sig,
                .pipelines = pipelines,
                .post_failure = .compiler_unavailable,
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
                .post_failure = .compile_failed,
            };
        };
        defer std.heap.c_allocator.free(entry_point_w);

        const custom_root_sig = post_root_sig orelse root_sig;

        // Why individual shaders dropped out, so an all-empty result can name
        // a cause. First failure wins: the notice names one reason and the
        // per-shader detail is already in the log.
        var loop_failure: ?renderer.CustomShaderFailure = null;

        for (custom_shaders) |source| {
            const dxil = compileHlslToDxil(alloc, source, entry_point_w) catch {
                if (loop_failure == null) loop_failure = .compile_failed;
                continue;
            } orelse {
                if (loop_failure == null) loop_failure = .compile_failed;
                continue;
            };
            // D3D12 copies bytecode into the PSO during CreateGraphicsPipelineState,
            // so the allocation can be freed immediately after PSO creation.
            defer alloc.free(dxil);

            const pso = Pipeline.init(.{
                .device = dev,
                .root_signature = custom_root_sig,
                .texture_tables = if (post_root_sig != null) &Pipeline.post_texture_tables else &Pipeline.main_texture_tables,
                .vs_bytecode = shader_bytecode.bg_color_vs,
                .ps_bytecode = dxil,
                .blend = .none,
            }) catch |err| {
                log.warn("custom shader PSO creation failed: {}", .{err});
                if (loop_failure == null) loop_failure = .pipeline_failed;
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
            // Only report a failure when nothing built at all. If some
            // shaders compiled the chain still runs, so a partial failure
            // belongs in the log rather than in a banner.
            .post_failure = if (post_pipelines.len == 0) loop_failure else null,
        };
    }

    pub fn deinit(self: *Shaders, alloc: std.mem.Allocator) void {
        if (self.defunct) return;

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

        self.* = uninit;
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

test "Shaders default reports no custom shader failure" {
    const s: Shaders = .{};
    try std.testing.expect(s.post_failure == null);
}

test "Shaders.init reports no failure when no shaders were requested" {
    // "Nothing configured" must never look like a failure -- that is the
    // distinction post_failure exists to draw, and an empty post_pipelines
    // slice alone cannot make it.
    const s = try Shaders.init(null, std.testing.allocator, &.{});
    try std.testing.expect(s.post_pipelines.len == 0);
    try std.testing.expect(s.post_failure == null);
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

/// A resource binding as the DXIL container's PSV0 part records it: the
/// runtime's own list of what a shader reads, after the compiler dropped
/// anything unused.
const PsvBinding = struct {
    /// PSVResourceType: 3 is a typed SRV (Texture2D), 5 a structured
    /// buffer, 1 a sampler, 2 a constant buffer.
    res_type: u32,
    space: u32,
    lower: u32,
    upper: u32,

    const srv_typed: u32 = 3;
};

fn readU32(bytes: []const u8, off: usize) !u32 {
    if (off + 4 > bytes.len) return error.Truncated;
    return std.mem.readInt(u32, bytes[off..][0..4], .little);
}

/// The PSV0 bindings of a DXIL container, into `out`. Returns how many.
fn psvBindings(dxil: []const u8, out: []PsvBinding) !usize {
    if (dxil.len < 32 or !std.mem.eql(u8, dxil[0..4], "DXBC")) return error.NotDxil;
    // magic, 16-byte digest, two u16 versions, container size, part count.
    const part_count = try readU32(dxil, 28);
    for (0..part_count) |i| {
        const off = try readU32(dxil, 32 + i * 4);
        if (off + 8 > dxil.len) return error.Truncated;
        if (!std.mem.eql(u8, dxil[off..][0..4], "PSV0")) continue;
        const size = try readU32(dxil, off + 4);
        if (off + 8 + size > dxil.len) return error.Truncated;
        const psv = dxil[off + 8 ..][0..size];
        const info_size = try readU32(psv, 0);
        var at: usize = 4 + info_size;
        const count = try readU32(psv, at);
        at += 4;
        if (count == 0) return 0;
        if (count > out.len) return error.TooManyBindings;
        const stride = try readU32(psv, at);
        at += 4;
        if (stride < 16) return error.Truncated;
        for (0..count) |r| {
            const base = at + r * stride;
            out[r] = .{
                .res_type = try readU32(psv, base),
                .space = try readU32(psv, base + 4),
                .lower = try readU32(psv, base + 8),
                .upper = try readU32(psv, base + 12),
            };
        }
        return count;
    }
    return error.NoPsv0;
}

test "SRV table: texture tables match the shaders" {
    // RenderPass binds Step.textures[i] to its own one-descriptor table at
    // register ti (Pipeline.zig pins the root signature side). This pins the
    // shader side: each pipeline's shaders read exactly the texture
    // registers the renderer hands it textures for, and none that has no
    // table. A shader that grows a texture without a table, or a table
    // widened back over its neighbours, fails here or in Pipeline.zig
    // before it can reach a device.
    if (comptime builtin.os.tag != .windows) return error.SkipZigTest;
    const Case = struct { name: []const u8, vs: []const u8, ps: []const u8, textures: u32 };
    const cases = [_]Case{
        .{ .name = "bg_color", .vs = shader_bytecode.bg_color_vs, .ps = shader_bytecode.bg_color_ps, .textures = 0 },
        .{ .name = "cell_bg", .vs = shader_bytecode.bg_color_vs, .ps = shader_bytecode.cell_bg_ps, .textures = 0 },
        .{ .name = "cell_text", .vs = shader_bytecode.cell_text_vs, .ps = shader_bytecode.cell_text_ps, .textures = 2 },
        .{ .name = "image", .vs = shader_bytecode.image_vs, .ps = shader_bytecode.image_ps, .textures = 1 },
        .{ .name = "bg_image", .vs = shader_bytecode.bg_image_vs, .ps = shader_bytecode.bg_image_ps, .textures = 1 },
    };
    var widest: u32 = 0;
    var seen_any = false;
    for (cases) |case| {
        var registers: u32 = 0; // bit r set: the shaders read texture tr
        for ([_][]const u8{ case.vs, case.ps }) |code| {
            var buf: [16]PsvBinding = undefined;
            const n = try psvBindings(code, &buf);
            for (buf[0..n]) |b| {
                if (b.res_type != PsvBinding.srv_typed) continue;
                seen_any = true;
                try std.testing.expectEqual(@as(u32, 0), b.space);
                var r = b.lower;
                while (r <= b.upper) : (r += 1) registers |= @as(u32, 1) << @intCast(r);
            }
        }
        const expected: u32 = (@as(u32, 1) << @intCast(case.textures)) - 1;
        if (registers != expected) {
            std.debug.print("{s}: shaders read texture registers 0x{x}, renderer binds 0x{x}\n", .{ case.name, registers, expected });
        }
        try std.testing.expectEqual(expected, registers);
        widest = @max(widest, case.textures);
    }
    // Non-vacuity: the parse found textures at all, and every table the
    // root signature declares is read by some shader.
    try std.testing.expect(seen_any);
    try std.testing.expectEqual(@as(usize, widest), Pipeline.main_texture_ranges.len);
}
