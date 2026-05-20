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
    /// Required when render_target is true. RTV descriptors are allocated from
    /// a separate RTV descriptor heap (D3D12_DESCRIPTOR_HEAP_TYPE_RTV).
    rtv_heap: ?*DescriptorHeap = null,
    pixel_format: dxgi.DXGI_FORMAT = .R8_UNORM,
    /// When true, the texture can be used as both a render target (via RTV)
    /// and a shader resource (via SRV). The resource is created with
    /// ALLOW_RENDER_TARGET flag. No initial data upload is performed.
    render_target: bool = false,
    /// When non-null, reuse this RTV descriptor slot instead of allocating
    /// a new one from the heap. Used during resize to avoid overwriting
    /// other frames' in-flight RTV descriptors.
    rtv_slot: ?DescriptorHeap.Descriptor = null,
    /// When non-null, reuse this SRV descriptor slot instead of allocating
    /// a new one from the heap. Used during resize to prevent SRV heap
    /// exhaustion from leaked descriptors.
    srv_slot: ?DescriptorHeap.Descriptor = null,
};

pub const Error = error{
    TextureCreateFailed,
    /// Texture upload failed: staging-buffer allocation or mapping,
    /// or a missing device/command_list/resource. Without this, the
    /// texture transitions to PIXEL_SHADER_RESOURCE with no contents
    /// and renders as a black quad.
    UploadFailed,
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
/// RTV descriptor for render-target binding. Only set when render_target is true.
rtv: DescriptorHeap.Descriptor = .{
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
/// Staging buffers from the most recent upload, one per row-band, kept
/// alive until the GPU finishes executing the CopyTextureRegion calls
/// that read from them. D3D12 does NOT extend resource lifetimes for
/// recorded commands, so each band's staging buffer must outlive command
/// list execution. All are released together at the start of the next
/// replaceRegion or in deinit. Backed by std.heap.c_allocator so deinit
/// stays signature-compatible with the value-receiver call sites
/// (Image switch captures, generic.zig front/back texture fields).
pending_staging: std.ArrayListUnmanaged(*d3d12.ID3D12Resource) = .empty,
/// Running sum of band-staging bytes currently held by `pending_staging`.
/// Mirrors the list contents so callers (image.zig State.upload) can
/// query the UPLOAD-heap pressure of an individual texture without
/// COM-querying each ID3D12Resource's size. Incremented in uploadRegion
/// per band; reset in replaceRegion's release loop.
pending_staging_bytes: u64 = 0,

/// Row-pitch alignment that DX12's CopyTextureRegion requires for staging
/// buffers (D3D12_TEXTURE_DATA_PITCH_ALIGNMENT). `pub` so image.zig can
/// `comptime assert` its mirror constant stays in lockstep.
pub const TEXTURE_DATA_PITCH_ALIGNMENT: u32 = 256;

/// Target size in bytes for each per-band staging buffer. Chosen to keep
/// individual UPLOAD-heap allocations small enough to succeed under
/// fragmentation while amortizing the per-band CopyTextureRegion cost.
/// At 8 MiB an RGBA image up to ~8192x256 (= 8 MiB) fits in one band;
/// larger images are split across multiple CopyTextureRegion calls.
const TEXTURE_UPLOAD_BAND_BYTES: u64 = 8 * 1024 * 1024;

pub fn init(opts: Options, width: usize, height: usize, data: ?[]const u8) Error!Texture {
    const device = opts.device orelse return error.TextureCreateFailed;
    const srv_heap = opts.srv_heap orelse return error.TextureCreateFailed;

    const bpp: u32 = bppForFormat(opts.pixel_format);
    const aligned_row_pitch = alignPitch(@intCast(width * bpp));

    // Create the GPU texture resource. Render targets use ALLOW_RENDER_TARGET.
    const resource = if (opts.render_target)
        createRenderTargetResource(device, @intCast(width), @intCast(height), opts.pixel_format) orelse return error.TextureCreateFailed
    else
        createTextureResource(device, @intCast(width), @intCast(height), opts.pixel_format) orelse return error.TextureCreateFailed;
    errdefer _ = resource.Release();

    // Allocate or reuse SRV descriptor.
    const srv = if (opts.srv_slot) |slot| slot else srv_heap.allocate() catch return error.TextureCreateFailed;

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

    // Create RTV if this is a render-target texture.
    var rtv: DescriptorHeap.Descriptor = .{
        .cpu = .{ .ptr = 0 },
        .gpu = .{ .ptr = 0 },
        .index = 0,
    };
    if (opts.render_target) {
        const rtv_heap = opts.rtv_heap orelse return error.TextureCreateFailed;
        if (opts.rtv_slot) |slot| {
            // Reuse a pre-allocated RTV descriptor slot (e.g. during resize).
            rtv = slot;
        } else {
            rtv = rtv_heap.allocate() catch return error.TextureCreateFailed;
        }
        device.CreateRenderTargetView(resource, null, rtv.cpu);
    }

    var tex = Texture{
        .width = width,
        .height = height,
        .bpp = bpp,
        .resource = resource,
        .srv = srv,
        .rtv = rtv,
        .aligned_row_pitch = aligned_row_pitch,
        .format = opts.pixel_format,
        .device = device,
        .command_list = opts.command_list,
        .state = if (opts.render_target)
            d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE
        else
            d3d12.D3D12_RESOURCE_STATES.COPY_DEST,
    };

    if (!opts.render_target) {
        // Upload initial data if provided. Propagate upload failures so
        // the caller doesn't end up with a texture that transitioned to
        // PIXEL_SHADER_RESOURCE without contents and renders as a black
        // quad. The errdefer above releases the GPU resource.
        if (data) |pixels| {
            try tex.uploadRegion(0, 0, @intCast(width), @intCast(height), pixels);
        }
        // Transition to shader-readable. The texture was created in COPY_DEST
        // so the initial upload (if any) could proceed without a barrier.
        tex.transition(d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE);
    }

    return tex;
}

pub fn deinit(self: Texture) void {
    for (self.pending_staging.items) |staging| {
        _ = staging.Release();
    }
    // deinit takes self by value to match Metal/OpenGL Texture and the
    // switch-capture call sites in image.zig. Copying self locally lets
    // us call ArrayListUnmanaged.deinit (pointer-receiver) without
    // changing the cross-backend signature.
    var self_mut = self;
    self_mut.pending_staging.deinit(std.heap.c_allocator);
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
/// The staging buffers are kept alive until the next replaceRegion call
/// or deinit, because D3D12 does not extend resource lifetimes for
/// recorded commands. The previous staging buffers are safe to release
/// here because the frame's fence wait in beginFrame guarantees the GPU
/// finished executing the prior CopyTextureRegion calls.
///
/// Returns error{}!void for API compatibility with Metal's replaceRegion
/// which cannot fail. DX12 upload failures are caught here and logged at
/// warn level so a font-atlas or render-target update that drops bytes
/// is visible without breaking the shared signature. Texture.init's
/// initial-data path propagates the same failure via uploadRegion, so
/// new-image uploads do not need this swallow.
///
/// Partial-write caveat: uploadRegion records one CopyTextureRegion per
/// row-band as it walks the data. If a later band fails (staging alloc,
/// Map, etc.) the earlier bands' copies stay recorded on the command
/// list and will execute when the frame submits. For Texture.init the
/// destination is errdefer-Released so the partial copies write to a
/// soon-released resource (harmless). For replaceRegion the destination
/// survives, so a multi-band atlas update could leave the texture with
/// the leading bands of the new content and the trailing rows of the
/// previous content. Acceptable for atlases (next replaceRegion fixes
/// it) but worth knowing if anyone calls replaceRegion on a multi-band
/// region of a user-visible texture.
pub fn replaceRegion(self: *Texture, x: usize, y: usize, width: usize, height: usize, data: []const u8) error{}!void {
    // Release the staging buffers from the previous upload. Safe because
    // beginFrame waited on the fence for this frame slot, so the GPU
    // has finished reading from them.
    for (self.pending_staging.items) |prev| {
        _ = prev.Release();
    }
    self.pending_staging.clearRetainingCapacity();
    self.pending_staging_bytes = 0;

    // Transition to COPY_DEST if needed.
    if (self.state != d3d12.D3D12_RESOURCE_STATES.COPY_DEST) {
        self.transition(d3d12.D3D12_RESOURCE_STATES.COPY_DEST);
    }

    self.uploadRegion(@intCast(x), @intCast(y), @intCast(width), @intCast(height), data) catch |err| {
        log.warn("replaceRegion upload dropped: {t}", .{err});
    };

    // Transition back to shader-readable.
    self.transition(d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE);
}

// --- Internal helpers ---

fn uploadRegion(self: *Texture, x: u32, y: u32, width: u32, height: u32, data: []const u8) Error!void {
    // Null guards are defensive: replaceRegion / Texture.init / State.upload
    // all handle error.UploadFailed by dropping the placement and marking
    // the image for unload (see image.zig State.upload), so this is a
    // recoverable diagnostic rather than a hard failure.
    const device = self.device orelse {
        log.warn("uploadRegion called with null device", .{});
        return error.UploadFailed;
    };
    const cmd_list = self.command_list orelse {
        log.warn("uploadRegion called with null command_list", .{});
        return error.UploadFailed;
    };
    const texture = self.resource orelse {
        log.warn("uploadRegion called with null resource", .{});
        return error.UploadFailed;
    };

    const region_aligned_pitch = alignPitch(width * self.bpp);
    const src_row_bytes: usize = width * self.bpp;
    const rows_per = rowsPerBand(region_aligned_pitch, TEXTURE_UPLOAD_BAND_BYTES);

    // Walk the upload region in ~TEXTURE_UPLOAD_BAND_BYTES row-bands.
    // Each band gets its own staging buffer + CopyTextureRegion call;
    // all stay alive in self.pending_staging until the next replaceRegion
    // or deinit. Splitting the upload keeps individual UPLOAD-heap
    // allocations small enough to succeed under heap fragmentation.
    //
    // Note the log-level asymmetry below: the null guards above log at
    // warn (defensive checks; never fire under correct callers), while
    // createStagingBuffer and Map failures log at err (real GPU/driver
    // failures with hex context worth investigating). Both still return
    // error.UploadFailed and let the caller drop the placement.
    var bands = Bands{ .height = height, .rows_per_band = rows_per };
    while (bands.next()) |band| {
        const band_size: u64 = @as(u64, region_aligned_pitch) * @as(u64, band.row_count);

        const staging = createStagingBuffer(device, band_size) orelse {
            log.err("failed to create staging buffer for texture upload (size={d})", .{band_size});
            return error.UploadFailed;
        };

        var mapped: ?*anyopaque = null;
        const read_range = d3d12.D3D12_RANGE{ .Begin = 0, .End = 0 };
        const map_hr = staging.Map(0, &read_range, &mapped);
        if (com.FAILED(map_hr) or mapped == null) {
            log.err("Map for staging buffer failed: 0x{x}", .{@as(u32, @bitCast(map_hr))});
            _ = staging.Release();
            return error.UploadFailed;
        }

        const dst: [*]u8 = @ptrCast(mapped.?);
        for (0..band.row_count) |row_in_band| {
            const src_row = @as(usize, band.start_row) + row_in_band;
            const dst_offset = row_in_band * @as(usize, region_aligned_pitch);
            const src_offset = src_row * src_row_bytes;
            @memcpy(dst[dst_offset..][0..src_row_bytes], data[src_offset..][0..src_row_bytes]);
        }
        staging.Unmap(0, null);

        const src_loc = d3d12.D3D12_TEXTURE_COPY_LOCATION{
            .pResource = staging,
            .Type = .PLACED_FOOTPRINT,
            .u = .{
                .PlacedFootprint = .{
                    .Offset = 0,
                    .Footprint = .{
                        .Format = self.format,
                        .Width = width,
                        .Height = band.row_count,
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
            .bottom = band.row_count,
            .back = 1,
        };

        // y + band.start_row is the band-to-destination offset arithmetic;
        // this is the integration point where the Bands iterator meets the
        // DX12 copy command, and is not covered by the iterator's pure
        // unit tests. A bug here would surface as vertically-shifted or
        // overlapping bands in a multi-band image -- worth eyeballing on
        // any change to this loop.
        cmd_list.CopyTextureRegion(&dst_loc, x, y + band.start_row, 0, &src_loc, &src_box);

        // Keep the band's staging buffer alive until the GPU finishes the
        // copy. Released together with the other bands at the start of
        // the next replaceRegion or in deinit. On append failure we must
        // release the just-created buffer to avoid leaking it.
        self.pending_staging.append(std.heap.c_allocator, staging) catch {
            _ = staging.Release();
            log.warn("failed to track staging buffer for chunked upload", .{});
            return error.UploadFailed;
        };
        self.pending_staging_bytes += band_size;
    }
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

/// Issue a resource barrier transition on the given command list.
/// Does NOT update self.state -- the caller is responsible for tracking
/// the resource state externally. Matches Target.transitionBarrier's pattern.
pub fn transitionBarrier(
    self: *const Texture,
    cl: *d3d12.ID3D12GraphicsCommandList,
    before: d3d12.D3D12_RESOURCE_STATES,
    after: d3d12.D3D12_RESOURCE_STATES,
) void {
    const resource = self.resource orelse return;
    if (before == after) return;
    const barrier = d3d12.D3D12_RESOURCE_BARRIER{
        .Type = .TRANSITION,
        .Flags = .NONE,
        .u = .{
            .Transition = .{
                .pResource = resource,
                .Subresource = 0xFFFFFFFF,
                .StateBefore = before,
                .StateAfter = after,
            },
        },
    };
    cl.ResourceBarrier(1, @ptrCast(&barrier));
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

/// Create a GPU texture resource that can be used as both a render target
/// and a shader resource. Uses ALLOW_RENDER_TARGET flag and starts in
/// RENDER_TARGET state.
fn createRenderTargetResource(device: *d3d12.ID3D12Device, width: u32, height: u32, format: dxgi.DXGI_FORMAT) ?*d3d12.ID3D12Resource {
    const heap_props = d3d12.D3D12_HEAP_PROPERTIES{
        .Type = .DEFAULT,
        .CPUPageProperty = 0,
        .MemoryPoolPreference = 0,
        .CreationNodeMask = 0,
        .VisibleNodeMask = 0,
    };

    const clear_value = d3d12.D3D12_CLEAR_VALUE{
        .Format = format,
        .u = .{ .Color = .{ 0.0, 0.0, 0.0, 0.0 } },
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
        .Flags = .ALLOW_RENDER_TARGET,
    };

    var resource: ?*d3d12.ID3D12Resource = null;
    const hr = device.CreateCommittedResource(
        &heap_props,
        0,
        &desc,
        d3d12.D3D12_RESOURCE_STATES.PIXEL_SHADER_RESOURCE,
        &clear_value,
        &d3d12.ID3D12Resource.IID,
        @ptrCast(&resource),
    );
    if (com.FAILED(hr)) {
        log.err("CreateCommittedResource for render target failed: 0x{x}", .{@as(u32, @bitCast(hr))});
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

/// Number of source rows that fit in a single upload staging band.
/// Guarantees at least 1 row even if a single row exceeds the budget,
/// so a pathologically wide image still makes forward progress (each
/// band uploads exactly one row).
fn rowsPerBand(aligned_row_pitch: u32, band_budget_bytes: u64) u32 {
    if (aligned_row_pitch == 0) return 1;
    const rows: u64 = band_budget_bytes / @as(u64, aligned_row_pitch);
    return @intCast(@max(@as(u64, 1), rows));
}

const Band = struct {
    start_row: u32,
    row_count: u32,
};

/// Iterator yielding row-bands for a chunked upload. `rows_per_band` comes
/// from `rowsPerBand`. The last band is truncated to the remaining rows.
const Bands = struct {
    height: u32,
    rows_per_band: u32,
    cursor: u32 = 0,

    fn next(self: *Bands) ?Band {
        if (self.cursor >= self.height) return null;
        const row_count = @min(self.rows_per_band, self.height - self.cursor);
        const band = Band{ .start_row = self.cursor, .row_count = row_count };
        self.cursor += row_count;
        return band;
    }
};

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

test "rowsPerBand divides evenly" {
    // 8 MiB budget / 256-byte row pitch = 32768 rows per band.
    try std.testing.expectEqual(@as(u32, 32768), rowsPerBand(256, 8 * 1024 * 1024));
}

test "rowsPerBand rounds down for non-divisible row pitch" {
    // 8 MiB budget / 700-byte row pitch = 11983, remainder 508.
    try std.testing.expectEqual(@as(u32, 11983), rowsPerBand(700, 8 * 1024 * 1024));
}

test "rowsPerBand floors at 1 when a single row exceeds the budget" {
    // A row that is itself 16 MiB still gets a 1-row band so progress is made.
    try std.testing.expectEqual(@as(u32, 1), rowsPerBand(16 * 1024 * 1024, 8 * 1024 * 1024));
}

test "rowsPerBand returns 1 when row exactly fills the budget" {
    // Exact-fit boundary -- the floor-1 path and the divide path both
    // yield 1, so this test pins the boundary behavior.
    try std.testing.expectEqual(@as(u32, 1), rowsPerBand(8 * 1024 * 1024, 8 * 1024 * 1024));
}

test "rowsPerBand returns 1 for zero pitch (defensive)" {
    try std.testing.expectEqual(@as(u32, 1), rowsPerBand(0, 8 * 1024 * 1024));
}

test "Bands iterates exact-multiple height" {
    var b = Bands{ .height = 64, .rows_per_band = 16 };
    try std.testing.expectEqualDeep(@as(?Band, .{ .start_row = 0, .row_count = 16 }), b.next());
    try std.testing.expectEqualDeep(@as(?Band, .{ .start_row = 16, .row_count = 16 }), b.next());
    try std.testing.expectEqualDeep(@as(?Band, .{ .start_row = 32, .row_count = 16 }), b.next());
    try std.testing.expectEqualDeep(@as(?Band, .{ .start_row = 48, .row_count = 16 }), b.next());
    try std.testing.expect(b.next() == null);
}

test "Bands truncates final band to remaining rows" {
    var b = Bands{ .height = 50, .rows_per_band = 16 };
    try std.testing.expectEqualDeep(@as(?Band, .{ .start_row = 0, .row_count = 16 }), b.next());
    try std.testing.expectEqualDeep(@as(?Band, .{ .start_row = 16, .row_count = 16 }), b.next());
    try std.testing.expectEqualDeep(@as(?Band, .{ .start_row = 32, .row_count = 16 }), b.next());
    try std.testing.expectEqualDeep(@as(?Band, .{ .start_row = 48, .row_count = 2 }), b.next());
    try std.testing.expect(b.next() == null);
}

test "Bands with single-band fit returns one band" {
    var b = Bands{ .height = 10, .rows_per_band = 100 };
    try std.testing.expectEqualDeep(@as(?Band, .{ .start_row = 0, .row_count = 10 }), b.next());
    try std.testing.expect(b.next() == null);
}

test "Bands with zero height yields nothing" {
    var b = Bands{ .height = 0, .rows_per_band = 16 };
    try std.testing.expect(b.next() == null);
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

test "Texture pending_staging defaults to empty" {
    const tex = Texture{};
    try std.testing.expectEqual(@as(usize, 0), tex.pending_staging.items.len);
}

test "Texture pending_staging_bytes defaults to 0" {
    const tex = Texture{};
    try std.testing.expectEqual(@as(u64, 0), tex.pending_staging_bytes);
}

test "Texture.Options defaults" {
    const opts = Options{};
    try std.testing.expect(opts.device == null);
    try std.testing.expect(opts.command_list == null);
    try std.testing.expect(opts.srv_heap == null);
    try std.testing.expectEqual(dxgi.DXGI_FORMAT.R8_UNORM, opts.pixel_format);
}

test "Error set carries UploadFailed" {
    // The variant exists so callers can react to a staging-buffer
    // allocation refusal rather than rendering a black quad.
    try std.testing.expectError(
        error.UploadFailed,
        @as(Error!void, error.UploadFailed),
    );
}

// Sentinel pointers stand in for real device/cmd_list/resource when the
// null-guard tests construct a Texture directly (skipping init). Both
// values are 8-byte aligned so Zig's @ptrFromInt pointer-alignment check
// accepts them; they differ only to stay grep-distinct in panic dumps.
const SENTINEL_A: usize = 0xDEAD0;
const SENTINEL_B: usize = 0xDEAD8;

test "uploadRegion returns UploadFailed when command_list is null" {
    var tex = Texture{
        .device = @ptrFromInt(SENTINEL_A),
        .command_list = null,
        .resource = @ptrFromInt(SENTINEL_B),
        .bpp = 4,
    };
    const bytes: [4]u8 = .{ 0, 0, 0, 0 };
    try std.testing.expectError(
        error.UploadFailed,
        tex.uploadRegion(0, 0, 1, 1, &bytes),
    );
}

test "uploadRegion returns UploadFailed when device is null" {
    var tex = Texture{
        .device = null,
        .command_list = @ptrFromInt(SENTINEL_A),
        .resource = @ptrFromInt(SENTINEL_B),
        .bpp = 4,
    };
    const bytes: [4]u8 = .{ 0, 0, 0, 0 };
    try std.testing.expectError(
        error.UploadFailed,
        tex.uploadRegion(0, 0, 1, 1, &bytes),
    );
}

test "uploadRegion returns UploadFailed when resource is null" {
    var tex = Texture{
        .device = @ptrFromInt(SENTINEL_A),
        .command_list = @ptrFromInt(SENTINEL_B),
        .resource = null,
        .bpp = 4,
    };
    const bytes: [4]u8 = .{ 0, 0, 0, 0 };
    try std.testing.expectError(
        error.UploadFailed,
        tex.uploadRegion(0, 0, 1, 1, &bytes),
    );
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

test "Texture.Options has render_target field" {
    const opts = Options{ .render_target = true };
    try std.testing.expect(opts.render_target);
}

test "Texture has rtv field" {
    try std.testing.expect(@hasField(Texture, "rtv"));
}

test "Texture default rtv is zero" {
    const tex = Texture{};
    try std.testing.expect(tex.rtv.cpu.ptr == 0);
}
