//! DX12 texture sampler backed by a descriptor in the sampler heap.
//!
//! In DX12, samplers are not standalone objects -- they are descriptors
//! written into a GPU-visible sampler heap. This struct tracks the
//! descriptor index and handles so the sampler can be bound via
//! SetGraphicsRootDescriptorTable.
const Sampler = @This();

const std = @import("std");

const d3d12 = @import("d3d12.zig");
const DescriptorHeap = @import("descriptor_heap.zig").DescriptorHeap;

const log = std.log.scoped(.directx12);

pub const Options = struct {
    device: ?*d3d12.ID3D12Device = null,
    sampler_heap: ?*DescriptorHeap = null,
    filter: d3d12.D3D12_FILTER = .MIN_MAG_MIP_LINEAR,
    address_mode_u: d3d12.D3D12_TEXTURE_ADDRESS_MODE = .CLAMP,
    address_mode_v: d3d12.D3D12_TEXTURE_ADDRESS_MODE = .CLAMP,
};

pub const Error = error{
    SamplerCreateFailed,
};

/// Descriptor handle for binding this sampler to the pipeline.
descriptor: DescriptorHeap.Descriptor = .{
    .cpu = .{ .ptr = 0 },
    .gpu = .{ .ptr = 0 },
    .index = 0,
},

pub fn init(opts: Options) Error!Sampler {
    const device = opts.device orelse return error.SamplerCreateFailed;
    const sampler_heap = opts.sampler_heap orelse return error.SamplerCreateFailed;

    const desc = sampler_heap.allocate() catch return error.SamplerCreateFailed;

    const sampler_desc = d3d12.D3D12_SAMPLER_DESC{
        .Filter = opts.filter,
        .AddressU = opts.address_mode_u,
        .AddressV = opts.address_mode_v,
        .AddressW = .CLAMP,
        .MipLODBias = 0.0,
        .MaxAnisotropy = 1,
        .ComparisonFunc = .NEVER,
        .BorderColor = .{ 0.0, 0.0, 0.0, 0.0 },
        .MinLOD = 0.0,
        // D3D12 default: allow all mip levels. Current textures use MipLevels=1
        // so this is a no-op, but correct for custom shader post-process sampling.
        .MaxLOD = 3.402823466e+38,
    };
    device.CreateSampler(&sampler_desc, desc.cpu);

    return .{
        .descriptor = desc,
    };
}

pub fn deinit(self: Sampler) void {
    // Sampler descriptors are owned by the heap's linear allocator --
    // freed when the heap is destroyed.
    _ = self;
}

// --- Tests ---

test "Sampler struct fields" {
    try std.testing.expect(@hasField(Sampler, "descriptor"));
}

test "Sampler.Options defaults" {
    const opts = Options{};
    try std.testing.expect(opts.device == null);
    try std.testing.expect(opts.sampler_heap == null);
    try std.testing.expectEqual(d3d12.D3D12_FILTER.MIN_MAG_MIP_LINEAR, opts.filter);
    try std.testing.expectEqual(d3d12.D3D12_TEXTURE_ADDRESS_MODE.CLAMP, opts.address_mode_u);
    try std.testing.expectEqual(d3d12.D3D12_TEXTURE_ADDRESS_MODE.CLAMP, opts.address_mode_v);
}
