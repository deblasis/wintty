//! GPU texture wrapper for DX11.
//!
//! Wraps an ID3D11Texture2D and its ID3D11ShaderResourceView. These are
//! always created as a pair: the texture holds pixel data, the SRV is
//! what shaders actually sample from. DX11 separates them because you
//! can have textures without SRVs (render targets) or multiple SRVs on
//! one texture (different format interpretations). For font atlas textures
//! we always need both, so we pair them.
//!
//! Uses DEFAULT usage (GPU-optimal memory) with UpdateSubresource for
//! uploads, rather than DYNAMIC + Map/Unmap. This is the opposite choice
//! from Buffer, and for good reason: textures are sampled every frame by
//! the pixel shader but only written when new glyphs are rasterized (rare
//! after the initial burst). DEFAULT textures sit in GPU-optimal memory
//! for fast sampling. The occasional UpdateSubresource cost is worth the
//! per-frame read speed.
const Self = @This();

const std = @import("std");
const d3d11 = @import("d3d11.zig");
const dxgi = @import("dxgi.zig");
const com = @import("com.zig");

const log = std.log.scoped(.directx11);

/// Options for initializing a texture.
pub const Options = struct {
    device: *d3d11.ID3D11Device,
    context: *d3d11.ID3D11DeviceContext,
    format: dxgi.DXGI_FORMAT = .B8G8R8A8_UNORM,
};

pub const Error = error{
    /// A DirectX 11 API call failed.
    DirectXFailed,
};

/// The underlying ID3D11Texture2D.
texture: *d3d11.ID3D11Texture2D,

/// Shader resource view for sampling this texture in shaders.
srv: *d3d11.ID3D11ShaderResourceView,

/// Saved options for replaceRegion (needs the context).
opts: Options,

/// The width of this texture in pixels.
width: usize,
/// The height of this texture in pixels.
height: usize,

/// Bytes per pixel, derived from the DXGI format at init time.
bpp: usize,

/// Initialize a texture.
///
/// Creates an ID3D11Texture2D with DEFAULT usage and a matching SRV.
/// If `data` is non-null, it's passed as initial data so the texture
/// is populated at creation time (no extra upload step needed).
pub fn init(
    opts: Options,
    width: usize,
    height: usize,
    data: ?[]const u8,
) Error!Self {
    const bpp = bppOf(opts.format);

    const desc = d3d11.D3D11_TEXTURE2D_DESC{
        .Width = @intCast(width),
        .Height = @intCast(height),
        .MipLevels = 1,
        .ArraySize = 1,
        .Format = opts.format,
        .SampleDesc = .{ .Count = 1, .Quality = 0 },
        .Usage = .DEFAULT,
        .BindFlags = d3d11.D3D11_BIND_SHADER_RESOURCE,
        .CPUAccessFlags = 0,
        .MiscFlags = 0,
    };

    // If we have initial data, provide it so the texture is populated
    // at creation time. This avoids an extra UpdateSubresource call.
    const initial_data: ?*const d3d11.D3D11_SUBRESOURCE_DATA = if (data) |d| &d3d11.D3D11_SUBRESOURCE_DATA{
        .pSysMem = @ptrCast(d.ptr),
        .SysMemPitch = @intCast(width * bpp),
        .SysMemSlicePitch = 0,
    } else null;

    var texture: ?*d3d11.ID3D11Texture2D = null;
    var hr = opts.device.CreateTexture2D(&desc, initial_data, &texture);
    if (com.FAILED(hr)) {
        log.err("CreateTexture2D failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
        return error.DirectXFailed;
    }
    const tex = texture orelse return error.DirectXFailed;

    // Create SRV so shaders can sample this texture.
    const srv_desc = d3d11.D3D11_SHADER_RESOURCE_VIEW_DESC{
        .Format = opts.format,
        .ViewDimension = .TEXTURE2D,
        .MostDetailedMip = 0,
        .MipLevels = 1,
    };

    var srv: ?*d3d11.ID3D11ShaderResourceView = null;
    hr = opts.device.CreateShaderResourceView(@ptrCast(tex), &srv_desc, &srv);
    if (com.FAILED(hr)) {
        log.err("CreateShaderResourceView failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
        _ = tex.Release();
        return error.DirectXFailed;
    }

    return .{
        .texture = tex,
        .srv = srv orelse {
            _ = tex.Release();
            return error.DirectXFailed;
        },
        .opts = opts,
        .width = width,
        .height = height,
        .bpp = bpp,
    };
}

/// Release both the SRV and texture.
/// SRV is released first (reverse creation order) to ensure
/// no dangling references to the underlying texture.
pub fn deinit(self: Self) void {
    _ = self.srv.Release();
    _ = self.texture.Release();
}

/// Replace a region of the texture with new data.
///
/// Uses UpdateSubresource with a D3D11_BOX to upload a sub-region.
/// This works with DEFAULT textures -- the runtime handles the staging
/// copy internally. For our use case (font atlas updates), this is
/// called when new glyphs are rasterized, which is infrequent after
/// the initial burst of text rendering.
pub fn replaceRegion(
    self: Self,
    x: usize,
    y: usize,
    width: usize,
    height: usize,
    data: []const u8,
) error{}!void {
    const box = d3d11.D3D11_BOX{
        .left = @intCast(x),
        .top = @intCast(y),
        .front = 0,
        .right = @intCast(x + width),
        .bottom = @intCast(y + height),
        .back = 1,
    };

    self.opts.context.UpdateSubresource(
        @ptrCast(self.texture),
        0,
        &box,
        @ptrCast(data.ptr),
        @intCast(width * self.bpp),
        0,
    );
}

/// Returns bytes per pixel for the given DXGI format.
///
/// Only the two formats used by the font atlas are supported.
/// Adding a new format is one switch arm -- no architectural change.
fn bppOf(format: dxgi.DXGI_FORMAT) usize {
    return switch (format) {
        .R8_UNORM => 1,
        .B8G8R8A8_UNORM => 4,
        .R8G8B8A8_UNORM => 4,
        else => std.debug.panic("unsupported DXGI_FORMAT for DX11 texture: {}", .{format}),
    };
}
