//! DX12 render pipeline -- root signature + Pipeline State Object (PSO).
//!
//! Each Pipeline wraps a single ID3D12PipelineState and a shared
//! ID3D12RootSignature. The root signature defines the resource binding
//! layout that all pipelines share:
//!
//!   Param 0: CBV at b0 (Uniforms constant buffer)
//!   Param 1: Descriptor table, one SRV at t0
//!   Param 2: Descriptor table for samplers at s0
//!   Param 3: Inline SRV at t3 (structured buffer, e.g. cells_bg)
//!   Param 4: Descriptor table, one SRV at t1
//!
//! Each texture register gets a table of its own, one descriptor wide, and
//! RenderPass binds each from the slot of the texture it samples. A table
//! therefore never covers a slot owned by anything but the texture bound
//! to it, which is what makes freeing a slot on the fence of the texture's
//! own retirement safe. See `main_texture_ranges`.
//!
//! This matches the HLSL register layout in shaders.hlsl. All five
//! pipelines (bg_color, cell_bg, cell_text, image, bg_image) share the
//! same root signature but have different PSOs with different shaders
//! and input layouts.
const Pipeline = @This();

const std = @import("std");
const builtin = @import("builtin");

const com = @import("com.zig");
const d3d12 = @import("d3d12.zig");
const dxgi = @import("dxgi.zig");

const HRESULT = com.HRESULT;
const FAILED = com.FAILED;

const log = std.log.scoped(.directx12);

/// The PSO for this pipeline, null if not yet created.
pso: ?*d3d12.ID3D12PipelineState = null,

/// Shared root signature. Owned by one Pipeline and referenced by
/// others -- the caller manages lifetime. Stored here so the pipeline
/// can bind it during draw calls.
root_signature: ?*d3d12.ID3D12RootSignature = null,

/// Root parameter of each texture table in `root_signature`, indexed by
/// shader register: RenderPass binds `Step.textures[i]` to entry i.
/// `main_texture_tables` or `post_texture_tables`.
texture_tables: []const u32 = &.{},

pub const Options = struct {
    device: *d3d12.ID3D12Device,
    root_signature: *d3d12.ID3D12RootSignature,
    /// Which texture tables `root_signature` has: `main_texture_tables` for the
    /// main root signature, `post_texture_tables` for the post one.
    texture_tables: []const u32,
    vs_bytecode: []const u8,
    ps_bytecode: []const u8,
    input_layout: ?[]const d3d12.D3D12_INPUT_ELEMENT_DESC = null,
    blend: BlendMode = .none,
    primitive_topology: d3d12.D3D12_PRIMITIVE_TOPOLOGY_TYPE = .TRIANGLE,
};

pub const BlendMode = enum {
    /// No blending -- output overwrites the render target.
    none,
    /// Premultiplied alpha: src=ONE, dst=INV_SRC_ALPHA.
    premultiplied_alpha,
};

/// Root parameter indices. Must match createRootSignature() layout.
pub const root_param_cbv: u32 = 0;
pub const root_param_srv_table: u32 = 1;
pub const root_param_sampler_table: u32 = 2;
pub const root_param_buffer_srv: u32 = 3;
pub const root_param_srv_table_t1: u32 = 4;

/// One descriptor at texture register `register`.
///
/// One wide on purpose. A range is not DESCRIPTORS_VOLATILE, so D3D12
/// requires every descriptor it covers to stay valid and unchanged until
/// the command list that bound it has finished, sampled or not. A wider
/// table bound from one texture's slot covers its neighbours too: slots
/// that belong to other textures, which their own retirement frees (and
/// rewrites as null SRVs) on a fence that says nothing about the frame
/// holding the table. That was a final release of a texture the GPU was
/// still bound to, and a rewrite of a static descriptor in flight.
fn textureRange(register: u32) d3d12.D3D12_DESCRIPTOR_RANGE1 {
    return .{
        .RangeType = .SRV,
        .NumDescriptors = 1,
        .BaseShaderRegister = register,
        .RegisterSpace = 0,
        // DATA_STATIC: a texture's contents do not change while a command
        // list that samples it executes.
        .Flags = .DATA_STATIC,
        .OffsetInDescriptorsFromTableStart = 0,
    };
}

/// The main root signature's texture ranges, indexed by register: t0 (the
/// grayscale atlas, the kitty image, the background image) and t1 (the
/// color atlas). Shader reflection pins this to what the shaders declare
/// (shaders.zig, "texture tables match the shaders").
pub const main_texture_ranges = [_]d3d12.D3D12_DESCRIPTOR_RANGE1{ textureRange(0), textureRange(1) };

/// The root parameter holding each of `main_texture_ranges`.
pub const main_texture_tables = [_]u32{ root_param_srv_table, root_param_srv_table_t1 };

/// The post root signature's single texture range (t0) and its parameter.
pub const post_texture_ranges = [_]d3d12.D3D12_DESCRIPTOR_RANGE1{textureRange(0)};
pub const post_texture_tables = [_]u32{root_param_srv_table};

/// s0, in both root signatures. NONE: the v1.1 default for samplers is
/// static descriptors.
const sampler_range = d3d12.D3D12_DESCRIPTOR_RANGE1{
    .RangeType = .SAMPLER,
    .NumDescriptors = 1,
    .BaseShaderRegister = 0,
    .RegisterSpace = 0,
    .Flags = .NONE,
    .OffsetInDescriptorsFromTableStart = 0,
};

/// The main root signature's parameters. See createRootSignature.
pub const main_root_params = [_]d3d12.D3D12_ROOT_PARAMETER1{
    // [0] Inline CBV at b0 -- binds with SetGraphicsRootConstantBufferView.
    // DATA_VOLATILE: uniform buffer changes every frame.
    .{
        .ParameterType = .CBV,
        .u = .{ .Descriptor = .{
            .ShaderRegister = 0,
            .RegisterSpace = 0,
            .Flags = .DATA_VOLATILE,
        } },
        .ShaderVisibility = .ALL,
    },
    // [1] Descriptor table for t0.
    //
    // PIXEL, not ALL: visibility is what decides the resource state the
    // debug layer demands at bind. ALL means any stage could read the
    // table, so it requires NON_PIXEL_SHADER_RESOURCE |
    // PIXEL_SHADER_RESOURCE, while these textures are only sampled from a
    // pixel shader (CellTextPS, ImagePS, BgImagePS) and so sit in
    // PIXEL_SHADER_RESOURCE alone.
    .{
        .ParameterType = .DESCRIPTOR_TABLE,
        .u = .{ .DescriptorTable = .{
            .NumDescriptorRanges = 1,
            .pDescriptorRanges = @ptrCast(&main_texture_ranges[0]),
        } },
        .ShaderVisibility = .PIXEL,
    },
    // [2] Descriptor table for samplers.
    .{
        .ParameterType = .DESCRIPTOR_TABLE,
        .u = .{ .DescriptorTable = .{
            .NumDescriptorRanges = 1,
            .pDescriptorRanges = @ptrCast(&sampler_range),
        } },
        .ShaderVisibility = .ALL,
    },
    // [3] Inline SRV for structured buffer data (cells_bg).
    // Binds with SetGraphicsRootShaderResourceView -- the GPU virtual
    // address is passed directly, no descriptor heap slot needed.
    // DATA_VOLATILE: the buffer binding changes per draw call.
    .{
        .ParameterType = .SRV,
        .u = .{ .Descriptor = .{
            .ShaderRegister = 3,
            .RegisterSpace = 0,
            .Flags = .DATA_VOLATILE,
        } },
        .ShaderVisibility = .ALL,
    },
    // [4] Descriptor table for t1, PIXEL for the same reason as [1].
    .{
        .ParameterType = .DESCRIPTOR_TABLE,
        .u = .{ .DescriptorTable = .{
            .NumDescriptorRanges = 1,
            .pDescriptorRanges = @ptrCast(&main_texture_ranges[1]),
        } },
        .ShaderVisibility = .PIXEL,
    },
};

/// Create the shared root signature used by all pipelines.
///
/// The layout is:
///   [0] CBV at b0 (inline root CBV -- just a GPU virtual address)
///   [1] Descriptor table: 1 SRV at t0
///   [2] Descriptor table: 1 sampler at s0
///   [3] Inline SRV at t3 (structured buffer data, e.g. cells_bg)
///   [4] Descriptor table: 1 SRV at t1
pub fn createRootSignature(device: *d3d12.ID3D12Device) !*d3d12.ID3D12RootSignature {
    const root_params = &main_root_params;
    const desc = d3d12.D3D12_VERSIONED_ROOT_SIGNATURE_DESC{
        .Version = .VERSION_1_1,
        .u = .{ .Desc_1_1 = .{
            .NumParameters = root_params.len,
            .pParameters = root_params,
            .NumStaticSamplers = 0,
            .pStaticSamplers = null,
            .Flags = .ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
        } },
    };

    // Serialize the versioned root signature to a blob.
    var blob: ?*d3d12.ID3DBlob = null;
    var error_blob: ?*d3d12.ID3DBlob = null;
    var hr = d3d12.D3D12SerializeVersionedRootSignature(
        &desc,
        &blob,
        &error_blob,
    );
    if (error_blob) |eb| {
        defer _ = eb.Release();
        const msg_ptr: [*]const u8 = @ptrCast(eb.GetBufferPointer());
        const msg_len = eb.GetBufferSize();
        log.err("Root signature serialization error: {s}", .{msg_ptr[0..msg_len]});
    }
    if (FAILED(hr)) {
        if (blob) |b| _ = b.Release();
        return error.RootSignatureSerializeFailed;
    }
    defer _ = blob.?.Release();

    // Create the root signature from the serialized blob.
    var root_sig: ?*d3d12.ID3D12RootSignature = null;
    hr = device.CreateRootSignature(
        0,
        blob.?.GetBufferPointer(),
        blob.?.GetBufferSize(),
        &d3d12.ID3D12RootSignature.IID,
        @ptrCast(&root_sig),
    );
    if (FAILED(hr)) {
        log.err("CreateRootSignature failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.RootSignatureCreationFailed;
    }

    return root_sig.?;
}

/// The post root signature's parameters. See createPostRootSignature.
pub const post_root_params = [_]d3d12.D3D12_ROOT_PARAMETER1{
    // [0] Inline CBV at b0 (binding=1 shifted to b0 by zioshade).
    .{
        .ParameterType = .CBV,
        .u = .{ .Descriptor = .{
            .ShaderRegister = 0,
            .RegisterSpace = 0,
            .Flags = .DATA_VOLATILE,
        } },
        .ShaderVisibility = .ALL,
    },
    // [1] Descriptor table: 1 SRV at t0 (source texture).
    // PIXEL for the same reason: only the post shader's pixel stage
    // samples t0, which endPass leaves in PIXEL_SHADER_RESOURCE.
    .{
        .ParameterType = .DESCRIPTOR_TABLE,
        .u = .{ .DescriptorTable = .{
            .NumDescriptorRanges = 1,
            .pDescriptorRanges = @ptrCast(&post_texture_ranges[0]),
        } },
        .ShaderVisibility = .PIXEL,
    },
    // [2] Descriptor table: 1 sampler at s0.
    .{
        .ParameterType = .DESCRIPTOR_TABLE,
        .u = .{ .DescriptorTable = .{
            .NumDescriptorRanges = 1,
            .pDescriptorRanges = @ptrCast(&sampler_range),
        } },
        .ShaderVisibility = .ALL,
    },
};

/// Root signature for custom post-process shaders.
/// zioshade applies binding_shift -1 during SPIR-V to HLSL, so binding=1
/// lands in register(b0) in the generated HLSL.
/// Layout:
///   [0] CBV at b0 (uniforms constant buffer)
///   [1] Descriptor table: 1 SRV at t0
///   [2] Descriptor table: 1 sampler at s0
pub fn createPostRootSignature(device: *d3d12.ID3D12Device) !*d3d12.ID3D12RootSignature {
    const root_params = &post_root_params;
    const desc = d3d12.D3D12_VERSIONED_ROOT_SIGNATURE_DESC{
        .Version = .VERSION_1_1,
        .u = .{ .Desc_1_1 = .{
            .NumParameters = root_params.len,
            .pParameters = root_params,
            .NumStaticSamplers = 0,
            .pStaticSamplers = null,
            .Flags = .ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
        } },
    };

    var blob: ?*d3d12.ID3DBlob = null;
    var error_blob: ?*d3d12.ID3DBlob = null;
    var hr = d3d12.D3D12SerializeVersionedRootSignature(
        &desc,
        &blob,
        &error_blob,
    );
    if (error_blob) |eb| {
        defer _ = eb.Release();
        const msg_ptr: [*]const u8 = @ptrCast(eb.GetBufferPointer());
        const msg_len = eb.GetBufferSize();
        log.err("Post root signature serialization error: {s}", .{msg_ptr[0..msg_len]});
    }
    if (FAILED(hr)) {
        if (blob) |b| _ = b.Release();
        return error.RootSignatureSerializeFailed;
    }
    defer _ = blob.?.Release();

    var root_sig: ?*d3d12.ID3D12RootSignature = null;
    hr = device.CreateRootSignature(
        0,
        blob.?.GetBufferPointer(),
        blob.?.GetBufferSize(),
        &d3d12.ID3D12RootSignature.IID,
        @ptrCast(&root_sig),
    );
    if (FAILED(hr)) {
        log.err("Post CreateRootSignature failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.RootSignatureCreationFailed;
    }

    return root_sig.?;
}

/// Create a pipeline with specific shaders and input layout.
pub fn init(opts: Options) !Pipeline {
    const no_blend = d3d12.D3D12_RENDER_TARGET_BLEND_DESC{
        .BlendEnable = .FALSE,
        .LogicOpEnable = .FALSE,
        .SrcBlend = .ONE,
        .DestBlend = .ZERO,
        .BlendOp = .ADD,
        .SrcBlendAlpha = .ONE,
        .DestBlendAlpha = .ZERO,
        .BlendOpAlpha = .ADD,
        .LogicOp = .NOOP,
        .RenderTargetWriteMask = @intFromEnum(d3d12.D3D12_COLOR_WRITE_ENABLE.ALL),
    };

    const premul_blend = d3d12.D3D12_RENDER_TARGET_BLEND_DESC{
        .BlendEnable = .TRUE,
        .LogicOpEnable = .FALSE,
        .SrcBlend = .ONE,
        .DestBlend = .INV_SRC_ALPHA,
        .BlendOp = .ADD,
        .SrcBlendAlpha = .ONE,
        .DestBlendAlpha = .INV_SRC_ALPHA,
        .BlendOpAlpha = .ADD,
        .LogicOp = .NOOP,
        .RenderTargetWriteMask = @intFromEnum(d3d12.D3D12_COLOR_WRITE_ENABLE.ALL),
    };

    const active_blend = switch (opts.blend) {
        .none => no_blend,
        .premultiplied_alpha => premul_blend,
    };

    // Build render target blend array -- only RT[0] is active.
    var rt_blends: [8]d3d12.D3D12_RENDER_TARGET_BLEND_DESC = undefined;
    rt_blends[0] = active_blend;
    for (1..8) |i| {
        rt_blends[i] = no_blend;
    }

    var rtv_formats: [8]dxgi.DXGI_FORMAT = undefined;
    rtv_formats[0] = .B8G8R8A8_UNORM;
    for (1..8) |i| {
        rtv_formats[i] = .UNKNOWN;
    }

    const pso_desc = d3d12.D3D12_GRAPHICS_PIPELINE_STATE_DESC{
        .pRootSignature = opts.root_signature,
        .VS = .{
            .pShaderBytecode = opts.vs_bytecode.ptr,
            .BytecodeLength = opts.vs_bytecode.len,
        },
        .PS = .{
            .pShaderBytecode = opts.ps_bytecode.ptr,
            .BytecodeLength = opts.ps_bytecode.len,
        },
        .DS = .{ .pShaderBytecode = null, .BytecodeLength = 0 },
        .HS = .{ .pShaderBytecode = null, .BytecodeLength = 0 },
        .GS = .{ .pShaderBytecode = null, .BytecodeLength = 0 },
        .StreamOutput = .{
            .pSODeclaration = null,
            .NumEntries = 0,
            .pBufferStrides = null,
            .NumStrides = 0,
            .RasterizedStream = 0,
        },
        .BlendState = .{
            .AlphaToCoverageEnable = .FALSE,
            .IndependentBlendEnable = .FALSE,
            .RenderTarget = rt_blends,
        },
        .SampleMask = 0xFFFFFFFF,
        .RasterizerState = .{
            .FillMode = .SOLID,
            .CullMode = .NONE,
            .FrontCounterClockwise = .FALSE,
            .DepthBias = 0,
            .DepthBiasClamp = 0.0,
            .SlopeScaledDepthBias = 0.0,
            .DepthClipEnable = .TRUE,
            .MultisampleEnable = .FALSE,
            .AntialiasedLineEnable = .FALSE,
            .ForcedSampleCount = 0,
            .ConservativeRaster = 0,
        },
        .DepthStencilState = std.mem.zeroes(d3d12.D3D12_DEPTH_STENCIL_DESC),
        .InputLayout = .{
            .pInputElementDescs = if (opts.input_layout) |il| il.ptr else null,
            .NumElements = if (opts.input_layout) |il| @intCast(il.len) else 0,
        },
        .IBStripCutValue = 0,
        .PrimitiveTopologyType = opts.primitive_topology,
        .NumRenderTargets = 1,
        .RTVFormats = rtv_formats,
        .DSVFormat = .UNKNOWN,
        .SampleDesc = .{ .Count = 1, .Quality = 0 },
        .NodeMask = 0,
        .CachedPSO = .{ .pCachedBlob = null, .CachedBlobSizeInBytes = 0 },
        .Flags = 0,
    };

    var pso: ?*d3d12.ID3D12PipelineState = null;
    const hr = opts.device.CreateGraphicsPipelineState(
        &pso_desc,
        &d3d12.ID3D12PipelineState.IID,
        @ptrCast(&pso),
    );
    if (FAILED(hr)) {
        log.err("CreateGraphicsPipelineState failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return error.PipelineStateCreationFailed;
    }

    return .{
        .pso = pso,
        .root_signature = opts.root_signature,
        .texture_tables = opts.texture_tables,
    };
}

pub fn deinit(self: Pipeline) void {
    if (self.pso) |pso| _ = pso.Release();
}

// --- Tests ---

test "Pipeline struct fields" {
    try std.testing.expect(@hasField(Pipeline, "pso"));
    try std.testing.expect(@hasField(Pipeline, "root_signature"));
}

test "Pipeline default is empty" {
    const p: Pipeline = .{};
    try std.testing.expect(p.pso == null);
    try std.testing.expect(p.root_signature == null);
}

test "root parameter indices" {
    try std.testing.expectEqual(@as(u32, 0), root_param_cbv);
    try std.testing.expectEqual(@as(u32, 1), root_param_srv_table);
    try std.testing.expectEqual(@as(u32, 2), root_param_sampler_table);
}

test "root_param_buffer_srv index" {
    try std.testing.expectEqual(@as(u32, 3), root_param_buffer_srv);
}

test "root_param_srv_table_t1 index" {
    try std.testing.expectEqual(@as(u32, 4), root_param_srv_table_t1);
}

test "every texture table is one descriptor wide, at the register it is indexed by" {
    // RenderPass binds Step.textures[i] from that texture's own slot to
    // table i. A range wider than one descriptor would cover the slots
    // after it, which belong to other textures, and a range at another
    // register would sample the wrong texture.
    const Sig = struct {
        params: []const d3d12.D3D12_ROOT_PARAMETER1,
        ranges: []const d3d12.D3D12_DESCRIPTOR_RANGE1,
        tables: []const u32,
    };
    const sigs = [_]Sig{
        .{ .params = &main_root_params, .ranges = &main_texture_ranges, .tables = &main_texture_tables },
        .{ .params = &post_root_params, .ranges = &post_texture_ranges, .tables = &post_texture_tables },
    };
    for (sigs) |sig| {
        try std.testing.expect(sig.ranges.len > 0);
        try std.testing.expectEqual(sig.ranges.len, sig.tables.len);
        for (sig.ranges, sig.tables, 0..) |*range, param, i| {
            try std.testing.expectEqual(d3d12.D3D12_DESCRIPTOR_RANGE_TYPE.SRV, range.RangeType);
            try std.testing.expectEqual(@as(u32, 1), range.NumDescriptors);
            try std.testing.expectEqual(@as(u32, @intCast(i)), range.BaseShaderRegister);
            try std.testing.expectEqual(@as(u32, 0), range.RegisterSpace);
            const p = sig.params[param];
            try std.testing.expectEqual(d3d12.D3D12_ROOT_PARAMETER_TYPE.DESCRIPTOR_TABLE, p.ParameterType);
            try std.testing.expectEqual(@as(u32, 1), p.u.DescriptorTable.NumDescriptorRanges);
            try std.testing.expectEqual(@intFromPtr(range), @intFromPtr(p.u.DescriptorTable.pDescriptorRanges.?));
        }
        // And no SRV table hides outside the list RenderPass binds from.
        var srv_tables: usize = 0;
        for (sig.params) |p| {
            if (p.ParameterType != .DESCRIPTOR_TABLE) continue;
            const ranges = p.u.DescriptorTable.pDescriptorRanges.?;
            for (ranges[0..p.u.DescriptorTable.NumDescriptorRanges]) |r| {
                if (r.RangeType == .SRV) srv_tables += 1;
            }
        }
        try std.testing.expectEqual(sig.tables.len, srv_tables);
    }
}

test "BlendMode values" {
    try std.testing.expectEqual(@as(u1, 0), @intFromEnum(BlendMode.none));
    try std.testing.expectEqual(@as(u1, 1), @intFromEnum(BlendMode.premultiplied_alpha));
}

test "deinit on default pipeline is safe" {
    const p: Pipeline = .{};
    p.deinit();
}

test "deinit does not touch root_signature" {
    // Shaders owns the shared root signature and releases it in
    // Shaders.deinit. Pipeline.deinit must only release the PSO.
    // If deinit tried to Release the root_signature this would crash
    // on the bogus pointer, failing the test.
    const sentinel: *d3d12.ID3D12RootSignature = @ptrFromInt(0xDEAD_BEF0);
    const p: Pipeline = .{ .pso = null, .root_signature = sentinel };
    p.deinit(); // must not dereference root_signature
}
