//! Texture sampler wrapper for DX11.
//!
//! Wraps an ID3D11SamplerState with hardcoded linear filtering and
//! clamp-to-edge addressing.
//!
//! Why hardcode instead of parameterize: Metal exposes filter/address
//! mode options because ObjC property setting makes it free. In DX11
//! it's a struct you fill out -- parameterizing just moves the same
//! constants from init() to the caller. The GenericRenderer only ever
//! creates one sampler with linear + clamp-to-edge (for font atlas
//! sampling). If we ever need a second configuration, adding Options
//! fields is trivial.
const Self = @This();

const std = @import("std");
const d3d11 = @import("d3d11.zig");
const com = @import("com.zig");

const log = std.log.scoped(.directx11);

/// Options for initializing a sampler.
pub const Options = struct {
    device: *d3d11.ID3D11Device,
};

pub const Error = error{
    /// A DirectX 11 API call failed.
    DirectXFailed,
};

/// The underlying ID3D11SamplerState.
sampler: *d3d11.ID3D11SamplerState,

pub fn init(opts: Options) Error!Self {
    const desc = d3d11.D3D11_SAMPLER_DESC{
        .Filter = .MIN_MAG_MIP_LINEAR,
        .AddressU = .CLAMP,
        .AddressV = .CLAMP,
        .AddressW = .CLAMP,
        .MipLODBias = 0.0,
        .MaxAnisotropy = 1,
        .ComparisonFunc = .NEVER, // unused with non-comparison filters, but 0 is not a valid enum value
        .BorderColor = .{ 0.0, 0.0, 0.0, 0.0 },
        .MinLOD = 0.0,
        .MaxLOD = std.math.floatMax(f32),
    };

    var sampler: ?*d3d11.ID3D11SamplerState = null;
    const hr = opts.device.CreateSamplerState(&desc, &sampler);
    if (com.FAILED(hr)) {
        log.err("CreateSamplerState failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
        return error.DirectXFailed;
    }

    return .{
        .sampler = sampler orelse return error.DirectXFailed,
    };
}

pub fn deinit(self: Self) void {
    _ = self.sampler.Release();
}
