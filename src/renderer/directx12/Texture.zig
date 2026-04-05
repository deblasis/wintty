//! DX12 GPU texture backed by a committed resource in DEFAULT heap.
//!
//! GPU-only memory (D3D12_HEAP_TYPE_DEFAULT). Uploads go through a
//! staging buffer in UPLOAD heap, copied via CopyTextureRegion on the
//! provided command list. The caller is responsible for executing the
//! command list and waiting for the GPU before releasing the staging buffer.
//!
//! Each texture owns an SRV descriptor allocated from the CBV/SRV/UAV heap.
const Texture = @This();

const std = @import("std");

const d3d12 = @import("d3d12.zig");
const dxgi = @import("dxgi.zig");
const com = @import("com.zig");
const DescriptorHeap = @import("descriptor_heap.zig").DescriptorHeap;

const log = std.log.scoped(.directx12);

pub const Options = struct {
    device: ?*d3d12.ID3D12Device = null,
    command_list: ?*d3d12.ID3D12GraphicsCommandList = null,
    srv_heap: ?*DescriptorHeap = null,
    pixel_format: dxgi.DXGI_FORMAT = .R8_UNORM,
};

pub const Error = error{
    TextureCreateFailed,
};

/// Width of this texture in pixels.
width: usize = 0,
/// Height of this texture in pixels.
height: usize = 0,
/// Bytes per pixel, derived from the pixel format.
bpp: u32 = 1,
/// The GPU texture resource (DEFAULT heap).
resource: ?*d3d12.ID3D12Resource = null,
/// SRV descriptor for shader binding.
srv: DescriptorHeap.Descriptor = .{
    .cpu = .{ .ptr = 0 },
    .gpu = .{ .ptr = 0 },
    .index = 0,
},
/// Row pitch aligned to D3D12_TEXTURE_DATA_PITCH_ALIGNMENT (256 bytes).
aligned_row_pitch: u32 = 0,
/// Pixel format of this texture.
format: dxgi.DXGI_FORMAT = .R8_UNORM,
/// Cached device pointer for replaceRegion uploads.
device: ?*d3d12.ID3D12Device = null,
/// Cached command list for replaceRegion uploads.
command_list: ?*d3d12.ID3D12GraphicsCommandList = null,
/// Current resource state for barrier tracking.
state: d3d12.D3D12_RESOURCE_STATES = d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE,
/// Staging buffer from the most recent upload, kept alive until the GPU
/// finishes executing the CopyTextureRegion that reads from it.
/// D3D12 does NOT extend resource lifetimes for recorded commands, so
/// the staging buffer must outlive the command list execution.
/// Released at the start of the next replaceRegion or in deinit.
pending_staging: ?*d3d12.ID3D12Resource = null,

const TEXTURE_DATA_PITCH_ALIGNMENT: u32 = 256;

pub fn init(opts: Options, width: usize, height: usize, data: ?[]const u8) Error!Texture {
    const device = opts.device orelse return error.TextureCreateFailed;
    const srv_heap = opts.srv_heap orelse return error.TextureCreateFailed;

    const bpp: u32 = bppForFormat(opts.pixel_format);
    const aligned_row_pitch = alignPitch(@intCast(width * bpp));

    // Create the GPU-only texture resource.
    const resource = createTextureResource(device, @intCast(width), @intCast(height), opts.pixel_format) orelse return error.TextureCreateFailed;
    errdefer _ = resource.Release();

    // Allocate SRV descriptor.
    // Note: the linear allocator has no individual free, so a failed init()
    // after this point permanently consumes one descriptor slot.
    const srv = srv_heap.allocate() catch return error.TextureCreateFailed;

    // Create the SRV.
    const srv_desc = d3d12.D3D12_SHADER_RESOURCE_VIEW_DESC{
        .Format = opts.pixel_format,
        .ViewDimension = .TEXTURE2D,
        .Shader4ComponentMapping = d3d12.D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING,
        .u = .{
            .Texture2D = .{
                .MostDetailedMip = 0,
                .MipLevels = 1,
                .PlaneSlice = 0,
                .ResourceMinLODClamp = 0.0,
            },
        },
    };
    device.CreateShaderResourceView(resource, &srv_desc, srv.cpu);

    var tex = Texture{
        .width = width,
        .height = height,
        .bpp = bpp,
        .resource = resource,
        .srv = srv,
        .aligned_row_pitch = aligned_row_pitch,
        .format = opts.pixel_format,
        .device = device,
        .command_list = opts.command_list,
        // Texture starts in COPY_DEST so the initial upload (if any) can proceed
        // without a barrier. After the upload we transition to PIXEL_SHADER_RESOURCE.
        .state = d3d12.D3D12_RESOURCE_STATES.COPY_DEST,
    };

    // Upload initial data if provided.
    if (data) |pixels| {
        tex.uploadRegion(0, 0, @intCast(width), @intCast(height), pixels);
    }
    // Transition to shader-readable. The texture was created in COPY_DEST
    // so the initial upload (if any) could proceed without a barrier.
    tex.transition(d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE);

    return tex;
}

pub fn deinit(self: Texture) void {
    if (self.pending_staging) |staging| {
        _ = staging.Release();
    }
    if (self.resource) |res| {
        _ = res.Release();
    }
    // SRV descriptor is owned by the heap's linear allocator --
    // it gets freed when the heap itself is destroyed.
}

/// Update the cached command list to the current frame's.
/// DX12 uses triple-buffered command lists that rotate each frame;
/// the texture must use the current frame's list, not a stale one
/// from init or a different frame slot.
pub fn setCommandList(self: *Texture, cl: ?*d3d12.ID3D12GraphicsCommandList) void {
    self.command_list = cl;
}

/// Upload pixel data to a sub-region of this texture.
///
/// The staging buffer is kept alive until the next replaceRegion call or
/// deinit, because D3D12 does not extend resource lifetimes for recorded
/// commands. The previous staging buffer is safe to release here because
/// the frame's fence wait in beginFrame guarantees the GPU finished
/// executing the prior CopyTextureRegion.
///
/// Returns error{}!void for API compatibility with Metal's replaceRegion
/// which cannot fail. DX12 upload failures are logged but not propagated.
pub fn replaceRegion(self: *Texture, x: usize, y: usize, width: usize, height: usize, data: []const u8) error{}!void {
    // Release the staging buffer from the previous upload. Safe because
    // beginFrame waited on the fence for this frame slot, so the GPU
    // has finished reading from it.
    if (self.pending_staging) |prev| {
        _ = prev.Release();
        self.pending_staging = null;
    }

    // Transition to COPY_DEST if needed.
    if (self.state != d3d12.D3D12_RESOURCE_STATES.COPY_DEST) {
        self.transition(d3d12.D3D12_RESOURCE_STATES.COPY_DEST);
    }

    self.uploadRegion(@intCast(x), @intCast(y), @intCast(width), @intCast(height), data);

    // Transition back to shader-readable.
    self.transition(d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE);
}

// --- Internal helpers ---

fn uploadRegion(self: *Texture, x: u32, y: u32, width: u32, height: u32, data: []const u8) void {
    const device = self.device orelse {
        log.err("uploadRegion called with null device", .{});
        return;
    };
    const cmd_list = self.command_list orelse {
        log.err("uploadRegion called with null command_list", .{});
        return;
    };
    const texture = self.resource orelse {
        log.err("uploadRegion called with null resource", .{});
        return;
    };

    const region_aligned_pitch = alignPitch(width * self.bpp);
    const staging_size: u64 = @as(u64, region_aligned_pitch) * @as(u64, height);

    // Create a temporary upload buffer for staging.
    const staging = createStagingBuffer(device, staging_size) orelse {
        log.err("failed to create staging buffer for texture upload (size={d})", .{staging_size});
        return;
    };
    // Staging buffer is saved to self.pending_staging after the copy is
    // recorded, and released at the start of the next replaceRegion or
    // in deinit (after the GPU has finished reading from it).

    // Map and copy row-by-row with pitch alignment.
    var mapped: ?*anyopaque = null;
    const read_range = d3d12.D3D12_RANGE{ .Begin = 0, .End = 0 };
    const map_hr = staging.Map(0, &read_range, &mapped);
    if (com.FAILED(map_hr) or mapped == null) {
        log.err("Map for staging buffer failed: 0x{x}", .{@as(u32, @bitCast(map_hr))});
        _ = staging.Release();
        return;
    }

    const dst: [*]u8 = @ptrCast(mapped.?);
    const src_row_bytes = width * self.bpp;
    for (0..height) |row| {
        const dst_offset = row * @as(usize, region_aligned_pitch);
        const src_offset = row * @as(usize, src_row_bytes);
        @memcpy(dst[dst_offset..][0..src_row_bytes], data[src_offset..][0..src_row_bytes]);
    }

    staging.Unmap(0, null);

    // Record the copy command.
    const src_loc = d3d12.D3D12_TEXTURE_COPY_LOCATION{
        .pResource = staging,
        .Type = .PLACED_FOOTPRINT,
        .u = .{
            .PlacedFootprint = .{
                .Offset = 0,
                .Footprint = .{
                    .Format = self.format,
                    .Width = width,
                    .Height = height,
                    .Depth = 1,
                    .RowPitch = region_aligned_pitch,
                },
            },
        },
    };

    const dst_loc = d3d12.D3D12_TEXTURE_COPY_LOCATION{
        .pResource = texture,
        .Type = .SUBRESOURCE_INDEX,
        .u = .{ .SubresourceIndex = 0 },
    };

    const src_box = d3d12.D3D12_BOX{
        .left = 0,
        .top = 0,
        .front = 0,
        .right = width,
        .bottom = height,
        .back = 1,
    };

    cmd_list.CopyTextureRegion(&dst_loc, x, y, 0, &src_loc, &src_box);

    // Keep the staging buffer alive until the GPU finishes the copy.
    // Released at the start of the next replaceRegion or in deinit.
    self.pending_staging = staging;
}

fn transition(self: *Texture, new_state: d3d12.D3D12_RESOURCE_STATES) void {
    const cmd_list = self.command_list orelse return;
    const resource = self.resource orelse return;

    if (self.state == new_state) return;

    const barrier = d3d12.D3D12_RESOURCE_BARRIER{
        .Type = .TRANSITION,
        .Flags = .NONE,
        .u = .{
            .Transition = .{
                .pResource = resource,
                .Subresource = 0xFFFFFFFF, // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
                .StateBefore = self.state,
                .StateAfter = new_state,
            },
        },
    };
    cmd_list.ResourceBarrier(1, @ptrCast(&barrier));
    self.state = new_state;
}

fn createTextureResource(device: *d3d12.ID3D12Device, width: u32, height: u32, format: dxgi.DXGI_FORMAT) ?*d3d12.ID3D12Resource {
    const heap_props = d3d12.D3D12_HEAP_PROPERTIES{
        .Type = .DEFAULT,
        .CPUPageProperty = 0,
        .MemoryPoolPreference = 0,
        .CreationNodeMask = 0,
        .VisibleNodeMask = 0,
    };

    const desc = d3d12.D3D12_RESOURCE_DESC{
        .Dimension = .TEXTURE2D,
        .Alignment = 0,
        .Width = width,
        .Height = height,
        .DepthOrArraySize = 1,
        .MipLevels = 1,
        .Format = format,
        .SampleDesc = .{ .Count = 1, .Quality = 0 },
        .Layout = .UNKNOWN,
        .Flags = .NONE,
    };

    var resource: ?*d3d12.ID3D12Resource = null;
    // Texture starts in COPY_DEST state so we can upload initial data.
    const hr = device.CreateCommittedResource(
        &heap_props,
        0,
        &desc,
        d3d12.D3D12_RESOURCE_STATES.COPY_DEST,
        null,
        &d3d12.ID3D12Resource.IID,
        @ptrCast(&resource),
    );
    if (com.FAILED(hr)) {
        log.err("CreateCommittedResource for texture failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return null;
    }
    return resource;
}

fn createStagingBuffer(device: *d3d12.ID3D12Device, size: u64) ?*d3d12.ID3D12Resource {
    const heap_props = d3d12.D3D12_HEAP_PROPERTIES{
        .Type = .UPLOAD,
        .CPUPageProperty = 0,
        .MemoryPoolPreference = 0,
        .CreationNodeMask = 0,
        .VisibleNodeMask = 0,
    };

    const desc = d3d12.D3D12_RESOURCE_DESC{
        .Dimension = .BUFFER,
        .Alignment = 0,
        .Width = size,
        .Height = 1,
        .DepthOrArraySize = 1,
        .MipLevels = 1,
        .Format = .UNKNOWN,
        .SampleDesc = .{ .Count = 1, .Quality = 0 },
        .Layout = .ROW_MAJOR,
        .Flags = .NONE,
    };

    var resource: ?*d3d12.ID3D12Resource = null;
    const hr = device.CreateCommittedResource(
        &heap_props,
        0,
        &desc,
        d3d12.D3D12_RESOURCE_STATES.GENERIC_READ,
        null,
        &d3d12.ID3D12Resource.IID,
        @ptrCast(&resource),
    );
    if (com.FAILED(hr)) {
        log.err("CreateCommittedResource for staging buffer failed: 0x{x}", .{@as(u32, @bitCast(hr))});
        return null;
    }
    return resource;
}

fn alignPitch(row_bytes: u32) u32 {
    return (row_bytes + TEXTURE_DATA_PITCH_ALIGNMENT - 1) & ~(TEXTURE_DATA_PITCH_ALIGNMENT - 1);
}

fn bppForFormat(format: dxgi.DXGI_FORMAT) u32 {
    return switch (format) {
        .R8_UNORM => 1,
        .R8G8B8A8_UNORM, .B8G8R8A8_UNORM => 4,
        else => {
            log.err("unhandled pixel format in bppForFormat, defaulting to 4 bpp", .{});
            return 4;
        },
    };
}

// --- Tests ---

test "alignPitch rounds up to 256" {
    try std.testing.expectEqual(@as(u32, 256), alignPitch(1));
    try std.testing.expectEqual(@as(u32, 256), alignPitch(256));
    try std.testing.expectEqual(@as(u32, 512), alignPitch(257));
    try std.testing.expectEqual(@as(u32, 1024), alignPitch(1000));
}

test "bppForFormat returns correct bytes per pixel" {
    try std.testing.expectEqual(@as(u32, 1), bppForFormat(.R8_UNORM));
    try std.testing.expectEqual(@as(u32, 4), bppForFormat(.R8G8B8A8_UNORM));
    try std.testing.expectEqual(@as(u32, 4), bppForFormat(.B8G8R8A8_UNORM));
}

test "Texture struct fields" {
    try std.testing.expect(@hasField(Texture, "width"));
    try std.testing.expect(@hasField(Texture, "height"));
    try std.testing.expect(@hasField(Texture, "resource"));
    try std.testing.expect(@hasField(Texture, "srv"));
    try std.testing.expect(@hasField(Texture, "aligned_row_pitch"));
    try std.testing.expect(@hasField(Texture, "state"));
    try std.testing.expect(@hasField(Texture, "pending_staging"));
}

test "Texture pending_staging defaults to null" {
    const tex = Texture{};
    try std.testing.expect(tex.pending_staging == null);
}

test "Texture.Options defaults" {
    const opts = Options{};
    try std.testing.expect(opts.device == null);
    try std.testing.expect(opts.command_list == null);
    try std.testing.expect(opts.srv_heap == null);
    try std.testing.expectEqual(dxgi.DXGI_FORMAT.R8_UNORM, opts.pixel_format);
}

test "setCommandList updates cached command list" {
    var tex = Texture{};
    try std.testing.expect(tex.command_list == null);
    // Use a sentinel to verify the field is written without a real device.
    const sentinel: *d3d12.ID3D12GraphicsCommandList = @ptrFromInt(0xDEAD0);
    tex.setCommandList(sentinel);
    try std.testing.expect(tex.command_list == sentinel);
    tex.setCommandList(null);
    try std.testing.expect(tex.command_list == null);
}
