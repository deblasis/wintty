//! Render target for DX11.
//!
//! Wraps an ID3D11RenderTargetView that draw commands render into.
//! For swap chain targets the RTV is owned by Device -- Target borrows
//! the pointer. For shared texture targets, Target owns the RTV and
//! its backing ID3D11Texture2D.
const std = @import("std");
const log = std.log.scoped(.directx11);
const com = @import("com.zig");
const d3d11 = @import("d3d11.zig");
const dxgi = @import("dxgi.zig");

const HANDLE = std.os.windows.HANDLE;
const HRESULT = com.HRESULT;

/// The render target view to draw into.
/// Null when running without a device (non-Windows builds).
rtv: ?*d3d11.ID3D11RenderTargetView = null,

/// Current width of this target in pixels.
width: usize = 0,
/// Current height of this target in pixels.
height: usize = 0,

/// The shared texture backing the RTV. Non-null only in owned mode
/// (shared texture surfaces). In borrowed mode (swap chain surfaces)
/// this is null -- Device owns the back buffer.
texture: ?*d3d11.ID3D11Texture2D = null,

/// Pointer to consumer's output slot for the DXGI shared handle.
/// Non-null only in owned mode. Updated on init and resize.
handle_out: ?*?HANDLE = null,

pub const SharedTextureError = error{
    TextureCreationFailed,
    SharedHandleFailed,
    RenderTargetViewFailed,
    QueryInterfaceFailed,
};

/// Create a shared texture and its RTV. Writes the DXGI shared
/// handle to *handle_out so the consumer can open it on their device.
pub fn initSharedTexture(
    device: *d3d11.ID3D11Device,
    width: u32,
    height: u32,
    handle_out: *?HANDLE,
) SharedTextureError!@This() {
    var self = @This(){};
    self.handle_out = handle_out;
    try self.createSharedTextureResources(device, width, height);
    return self;
}

/// Recreate the shared texture at a new size. Releases old resources
/// and writes the new DXGI shared handle to *handle_out.
pub fn resizeSharedTexture(
    self: *@This(),
    device: *d3d11.ID3D11Device,
    width: u32,
    height: u32,
) SharedTextureError!void {
    self.releaseOwnedResources();
    try self.createSharedTextureResources(device, width, height);
}

pub fn deinit(self: *@This()) void {
    self.releaseOwnedResources();
    self.* = undefined;
}

/// Release texture and RTV if this Target owns them (shared texture mode).
/// No-op in borrowed mode (swap chain surfaces).
fn releaseOwnedResources(self: *@This()) void {
    if (self.rtv) |rtv| {
        if (self.texture != null) {
            // Owned mode: we created this RTV, so we release it.
            _ = rtv.Release();
            self.rtv = null;
        }
        // Borrowed mode: RTV belongs to Device, don't release.
    }
    if (self.texture) |tex| {
        _ = tex.Release();
        self.texture = null;
    }
}

/// Create the D3D11 texture, RTV, and query the shared handle.
/// Used by both initSharedTexture and resizeSharedTexture.
fn createSharedTextureResources(
    self: *@This(),
    device: *d3d11.ID3D11Device,
    width: u32,
    height: u32,
) SharedTextureError!void {
    // Create a render-target texture with the shared flag so consumers
    // can open it on their own D3D11 device via OpenSharedResource.
    const desc = d3d11.D3D11_TEXTURE2D_DESC{
        .Width = width,
        .Height = height,
        .MipLevels = 1,
        .ArraySize = 1,
        .Format = .B8G8R8A8_UNORM,
        .SampleDesc = .{ .Count = 1, .Quality = 0 },
        .Usage = .DEFAULT,
        .BindFlags = d3d11.D3D11_BIND_RENDER_TARGET | d3d11.D3D11_BIND_SHADER_RESOURCE,
        .CPUAccessFlags = 0,
        .MiscFlags = d3d11.D3D11_RESOURCE_MISC_SHARED,
    };

    var texture_opt: ?*d3d11.ID3D11Texture2D = null;
    var hr = device.CreateTexture2D(&desc, null, &texture_opt);
    if (com.FAILED(hr) or texture_opt == null) {
        log.err("CreateTexture2D (shared) failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
        return SharedTextureError.TextureCreationFailed;
    }
    const texture = texture_opt.?;
    errdefer _ = texture.Release();

    // Create render target view from the texture.
    var rtv_opt: ?*d3d11.ID3D11RenderTargetView = null;
    hr = device.CreateRenderTargetView(@ptrCast(texture), null, &rtv_opt);
    if (com.FAILED(hr) or rtv_opt == null) {
        log.err("CreateRenderTargetView (shared texture) failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
        return SharedTextureError.RenderTargetViewFailed;
    }
    errdefer _ = rtv_opt.?.Release();

    // Query IDXGIResource to get the shared handle.
    var dxgi_resource_opt: ?*anyopaque = null;
    hr = texture.vtable.QueryInterface(texture, &dxgi.IDXGIResource.IID, &dxgi_resource_opt);
    if (com.FAILED(hr) or dxgi_resource_opt == null) {
        log.err("QI for IDXGIResource failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
        return SharedTextureError.QueryInterfaceFailed;
    }
    const dxgi_resource: *dxgi.IDXGIResource = @ptrCast(@alignCast(dxgi_resource_opt.?));
    defer _ = dxgi_resource.Release();

    var shared_handle: ?HANDLE = null;
    hr = dxgi_resource.GetSharedHandle(&shared_handle);
    if (com.FAILED(hr)) {
        log.err("GetSharedHandle failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
        return SharedTextureError.SharedHandleFailed;
    }

    // Write the handle to the consumer's output slot.
    self.handle_out.?.* = shared_handle;

    self.texture = texture;
    self.rtv = rtv_opt.?;
    self.width = @intCast(width);
    self.height = @intCast(height);

    log.info("shared texture created: {}x{}", .{ width, height });
}

fn createTestDevice() !*d3d11.ID3D11Device {
    if (@import("builtin").os.tag != .windows) return error.SkipZigTest;

    var device_opt: ?*d3d11.ID3D11Device = null;
    var context_opt: ?*d3d11.ID3D11DeviceContext = null;
    const feature_levels = [_]d3d11.D3D_FEATURE_LEVEL{.@"11_0"};
    const hr = d3d11.D3D11CreateDevice(
        null,
        .HARDWARE,
        null,
        d3d11.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        &feature_levels,
        feature_levels.len,
        d3d11.D3D11_SDK_VERSION,
        &device_opt,
        null,
        &context_opt,
    );
    if (com.FAILED(hr)) return error.SkipZigTest;
    // Release the context -- Target tests only need the device.
    if (context_opt) |ctx| _ = ctx.Release();
    return device_opt orelse error.SkipZigTest;
}

test "shared texture lifecycle" {
    const device = createTestDevice() catch return;
    defer _ = device.Release();

    // Init shared texture.
    var shared_handle: ?HANDLE = null;
    var target = try initSharedTexture(device, 640, 480, &shared_handle);

    try std.testing.expect(target.rtv != null);
    try std.testing.expect(target.texture != null);
    try std.testing.expect(shared_handle != null);
    try std.testing.expectEqual(@as(usize, 640), target.width);
    try std.testing.expectEqual(@as(usize, 480), target.height);

    // Resize -- new texture means new shared handle.
    const old_handle = shared_handle;
    try target.resizeSharedTexture(device, 1280, 720);

    try std.testing.expect(target.rtv != null);
    try std.testing.expect(target.texture != null);
    try std.testing.expectEqual(@as(usize, 1280), target.width);
    try std.testing.expectEqual(@as(usize, 720), target.height);
    try std.testing.expect(shared_handle != null);
    // Handle must differ after resize (new texture = new DXGI resource).
    try std.testing.expect(shared_handle != old_handle);

    // Deinit -- self.* = undefined catches double-deinit in debug.
    target.deinit();
}

test "borrowed target deinit does not release owned resources" {
    const device = createTestDevice() catch return;
    defer _ = device.Release();

    var shared_handle: ?HANDLE = null;
    var owner = try initSharedTexture(device, 640, 480, &shared_handle);
    defer owner.deinit();

    // Create a borrowed view the same way initTarget does: copy rtv and
    // dimensions but leave texture/handle_out null.
    var borrowed = @This(){
        .rtv = owner.rtv,
        .width = owner.width,
        .height = owner.height,
    };

    // Deinit on the borrowed view must not release the RTV (texture is
    // null so releaseOwnedResources treats it as borrowed).
    borrowed.deinit();

    // Owner's resources must still be intact after borrowed deinit.
    try std.testing.expect(owner.rtv != null);
    try std.testing.expect(owner.texture != null);
}
