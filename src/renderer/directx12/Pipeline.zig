//! DX12 render pipeline -- root signature + Pipeline State Object (PSO).
//!
//! Each Pipeline wraps a single ID3D12PipelineState and a shared
//! ID3D12RootSignature. The root signature defines the resource binding
//! layout that all pipelines share:
//!
//!   Param 0: CBV at b0 (Uniforms constant buffer)
//!   Param 1: Descriptor table for SRVs at t0..t2 (textures, structured buffers)
//!   Param 2: Descriptor table for samplers at s0
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

pub const Options = struct {
    device: *d3d12.ID3D12Device,
    root_signature: *d3d12.ID3D12RootSignature,
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

/// Number of SRV slots in the descriptor table (t0..t2).
pub const srv_table_size: u32 = 3;

/// Root parameter indices. Must match createRootSignature() layout.
pub const root_param_cbv: u32 = 0;
pub const root_param_srv_table: u32 = 1;
pub const root_param_sampler_table: u32 = 2;

/// Create the shared root signature used by all pipelines.
///
/// The layout is:
///   [0] CBV at b0 (inline root CBV -- just a GPU virtual address)
///   [1] Descriptor table: 3 SRVs at t0, t1, t2
///   [2] Descriptor table: 1 sampler at s0
pub fn createRootSignature(device: *d3d12.ID3D12Device) !*d3d12.ID3D12RootSignature {
    // SRV range: t0..t2 (textures and structured buffers).
    const srv_range = d3d12.D3D12_DESCRIPTOR_RANGE{
        .RangeType = .SRV,
        .NumDescriptors = srv_table_size,
        .BaseShaderRegister = 0,
        .RegisterSpace = 0,
        .OffsetInDescriptorsFromTableStart = 0,
    };

    // Sampler range: s0.
    const sampler_range = d3d12.D3D12_DESCRIPTOR_RANGE{
        .RangeType = .SAMPLER,
        .NumDescriptors = 1,
        .BaseShaderRegister = 0,
        .RegisterSpace = 0,
        .OffsetInDescriptorsFromTableStart = 0,
    };

    const root_params = [_]d3d12.D3D12_ROOT_PARAMETER{
        // [0] Inline CBV at b0 -- binds with SetGraphicsRootConstantBufferView.
        .{
            .ParameterType = .CBV,
            .u = .{ .Descriptor = .{
                .ShaderRegister = 0,
                .RegisterSpace = 0,
            } },
            .ShaderVisibility = .ALL,
        },
        // [1] Descriptor table for SRVs.
        .{
            .ParameterType = .DESCRIPTOR_TABLE,
            .u = .{ .DescriptorTable = .{
                .NumDescriptorRanges = 1,
                .pDescriptorRanges = @ptrCast(&srv_range),
            } },
            .ShaderVisibility = .ALL,
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
    };

    const desc = d3d12.D3D12_ROOT_SIGNATURE_DESC{
        .NumParameters = root_params.len,
        .pParameters = &root_params,
        .NumStaticSamplers = 0,
        .pStaticSamplers = null,
        .Flags = .ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT,
    };

    // Serialize the root signature to a blob.
    var blob: ?*d3d12.ID3DBlob = null;
    var error_blob: ?*d3d12.ID3DBlob = null;
    // TODO(#127): upgrade to D3D_ROOT_SIGNATURE_VERSION_1_1 for DATA_STATIC /
    // DESCRIPTORS_VOLATILE flags -- allows driver to optimize descriptor
    // access for our workload (static atlas textures, per-frame uniforms).
    var hr = d3d12.D3D12SerializeRootSignature(
        &desc,
        1, // D3D_ROOT_SIGNATURE_VERSION_1
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

/// Create a pipeline with specific shaders and input layout.
pub fn init(opts: Options) !Pipeline {
    const no_blend = d3d12.D3D12_RENDER_TARGET_BLEND_DESC{
        .BlendEnable = 0,
        .LogicOpEnable = 0,
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
        .BlendEnable = 1,
        .LogicOpEnable = 0,
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
            .AlphaToCoverageEnable = 0,
            .IndependentBlendEnable = 0,
            .RenderTarget = rt_blends,
        },
        .SampleMask = 0xFFFFFFFF,
        .RasterizerState = .{
            .FillMode = .SOLID,
            .CullMode = .NONE,
            .FrontCounterClockwise = 0,
            .DepthBias = 0,
            .DepthBiasClamp = 0.0,
            .SlopeScaledDepthBias = 0.0,
            .DepthClipEnable = 1,
            .MultisampleEnable = 0,
            .AntialiasedLineEnable = 0,
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

test "srv_table_size covers t0..t2" {
    try std.testing.expectEqual(@as(u32, 3), srv_table_size);
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
