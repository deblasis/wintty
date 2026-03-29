const std = @import("std");
const d3d11 = @import("d3d11.zig");
const com = @import("com.zig");

const log = std.log.scoped(.directx11);

/// Type-erased buffer handle for passing to RenderPass.Step.
/// Mirrors Metal's objc.Object and OpenGL's gl.Buffer -- lets
/// GenericRenderer pass uniform/vertex buffers without knowing T.
///
/// Carries an optional SRV for buffers that are bound as
/// StructuredBuffer in HLSL (e.g. cell_bg_colors at register(t0)).
/// Vertex and constant buffers leave srv as null.
pub const RawBuffer = struct {
    ptr: *d3d11.ID3D11Buffer,
    srv: ?*d3d11.ID3D11ShaderResourceView = null,
};

/// Options for initializing a buffer.
pub const Options = struct {
    device: *d3d11.ID3D11Device,
    context: *d3d11.ID3D11DeviceContext,
    usage: Usage = .dynamic,
    bind_flags: d3d11.D3D11_BIND_FLAG = d3d11.D3D11_BIND_VERTEX_BUFFER,
    /// Set to @sizeOf(T) for StructuredBuffer bindings.
    /// When non-zero, the buffer is created with MISC_BUFFER_STRUCTURED
    /// and an SRV is created for shader access.
    structure_byte_stride: u32 = 0,
};

pub const Usage = enum {
    /// CPU writes every frame via Map/Unmap with WRITE_DISCARD.
    /// The driver internally rotates allocations to avoid CPU/GPU stalls,
    /// similar to Metal's explicit triple-buffering.
    dynamic,
    // default and immutable are not yet needed. Add them when we have
    // a consumer (e.g. static vertex data for screen quads).
};

/// DX11 GPU data buffer for a set of equal-typed elements.
///
/// Wraps an ID3D11Buffer with DYNAMIC usage and CPU_ACCESS_WRITE.
/// The buffer is parameterized over element type T so that sync/map
/// operations work in terms of typed slices rather than raw bytes.
///
/// Why DYNAMIC + Map/Unmap instead of DEFAULT + UpdateSubresource:
/// Buffers are written every frame (uniforms, cell instance data).
/// Map/Unmap is one copy (CPU -> driver-managed memory).
/// UpdateSubresource does CPU -> staging -> GPU, an extra copy per frame.
/// DYNAMIC buffers live in CPU-accessible memory, which the GPU reads
/// slightly slower, but for data that changes every frame this is the
/// right trade-off.
pub fn Buffer(comptime T: type) type {
    return struct {
        const Self = @This();

        /// The options this buffer was initialized with.
        opts: Options,

        /// The underlying ID3D11Buffer and optional SRV for structured buffers.
        buffer: RawBuffer,

        /// The allocated capacity in number of T elements (not bytes).
        len: usize,

        pub fn init(opts: Options, len: usize) !Self {
            const byte_size = validateAndComputeSize(opts, len);
            const buf = try createBuffer(opts, byte_size, null);
            const srv = try createSRV(opts, buf, len);
            return .{ .opts = opts, .buffer = .{ .ptr = buf, .srv = srv }, .len = len };
        }

        /// Init the buffer filled with the given data.
        pub fn initFill(opts: Options, data: []const T) !Self {
            const byte_size = validateAndComputeSize(opts, data.len);
            const initial_data = d3d11.D3D11_SUBRESOURCE_DATA{
                .pSysMem = @ptrCast(data.ptr),
                .SysMemPitch = 0,
                .SysMemSlicePitch = 0,
            };
            const buf = try createBuffer(opts, byte_size, &initial_data);
            const srv = try createSRV(opts, buf, data.len);
            return .{ .opts = opts, .buffer = .{ .ptr = buf, .srv = srv }, .len = data.len };
        }

        pub fn deinit(self: *const Self) void {
            if (self.buffer.srv) |srv| _ = srv.Release();
            _ = self.buffer.ptr.Release();
        }

        /// Sync new contents to the buffer, replacing everything.
        ///
        /// If the data exceeds current capacity, the buffer is reallocated
        /// at 2x the required size. This amortizes reallocation cost over
        /// the lifetime of the buffer (same strategy as Metal and std.ArrayList).
        ///
        /// Uses Map with WRITE_DISCARD, which tells the driver "I'm replacing
        /// the entire buffer." The driver hands back a fresh allocation from
        /// its internal pool so there's no stall even if the GPU is still
        /// reading the previous frame's data.
        pub fn sync(self: *Self, data: []const T) !void {
            const req_bytes = data.len * @sizeOf(T);

            // If we need more space than our buffer has, reallocate.
            // WRITE_DISCARD rotates which allocation you write to, but
            // it can't grow the allocation -- that requires a new buffer.
            if (req_bytes > self.len * @sizeOf(T)) {
                if (self.buffer.srv) |srv| _ = srv.Release();
                _ = self.buffer.ptr.Release();
                // Allocate 2x what we need to amortize future growth.
                const new_len = data.len * 2;
                const new_byte_size = validateAndComputeSize(self.opts, new_len);
                const buf = try createBuffer(self.opts, new_byte_size, null);
                const srv = try createSRV(self.opts, buf, new_len);
                self.buffer = .{ .ptr = buf, .srv = srv };
                self.len = new_len;
            }

            // Map the buffer for writing. WRITE_DISCARD means we get a
            // fresh allocation -- no need to worry about what the GPU is reading.
            var mapped: d3d11.D3D11_MAPPED_SUBRESOURCE = undefined;
            const hr = self.opts.context.Map(
                @ptrCast(self.buffer.ptr),
                0,
                .WRITE_DISCARD,
                0,
                &mapped,
            );
            if (com.FAILED(hr)) {
                log.err("Buffer.sync Map failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
                return error.DirectXFailed;
            }

            const dst: [*]u8 = @ptrCast(mapped.pData orelse {
                log.warn("Buffer.sync: Map returned null pData", .{});
                self.opts.context.Unmap(@ptrCast(self.buffer.ptr), 0);
                return error.DirectXFailed;
            });
            const src: [*]const u8 = @ptrCast(data.ptr);
            @memcpy(dst[0..req_bytes], src[0..req_bytes]);

            self.opts.context.Unmap(@ptrCast(self.buffer.ptr), 0);
        }

        /// Like sync but takes data from an array of ArrayLists.
        /// Returns the number of items synced.
        ///
        /// Used by GenericRenderer to flatten per-row cell data
        /// (stored as separate ArrayLists) into a single GPU buffer.
        pub fn syncFromArrayLists(self: *Self, lists: []const std.ArrayListUnmanaged(T)) !usize {
            var total_len: usize = 0;
            for (lists) |list| {
                total_len += list.items.len;
            }

            const req_bytes = total_len * @sizeOf(T);

            if (req_bytes > self.len * @sizeOf(T)) {
                if (self.buffer.srv) |srv| _ = srv.Release();
                _ = self.buffer.ptr.Release();
                const new_len = total_len * 2;
                const new_byte_size = validateAndComputeSize(self.opts, new_len);
                const buf = try createBuffer(self.opts, new_byte_size, null);
                const new_srv = try createSRV(self.opts, buf, new_len);
                self.buffer = .{ .ptr = buf, .srv = new_srv };
                self.len = new_len;
            }

            var mapped: d3d11.D3D11_MAPPED_SUBRESOURCE = undefined;
            const hr = self.opts.context.Map(
                @ptrCast(self.buffer.ptr),
                0,
                .WRITE_DISCARD,
                0,
                &mapped,
            );
            if (com.FAILED(hr)) {
                log.err("Buffer.syncFromArrayLists Map failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
                return error.DirectXFailed;
            }

            const dst: [*]u8 = @ptrCast(mapped.pData orelse {
                log.warn("Buffer.syncFromArrayLists: Map returned null pData", .{});
                self.opts.context.Unmap(@ptrCast(self.buffer.ptr), 0);
                return error.DirectXFailed;
            });

            var offset: usize = 0;
            for (lists) |list| {
                const chunk_bytes = list.items.len * @sizeOf(T);
                const src: [*]const u8 = @ptrCast(list.items.ptr);
                @memcpy(dst[offset..][0..chunk_bytes], src[0..chunk_bytes]);
                offset += chunk_bytes;
            }

            self.opts.context.Unmap(@ptrCast(self.buffer.ptr), 0);

            return total_len;
        }

        /// Map the buffer for direct write access.
        ///
        /// Returns a typed slice. The caller MUST call unmap() when done.
        /// Uses WRITE_DISCARD so the previous contents are invalidated.
        pub fn map(self: *Self, len: usize) ![]T {
            std.debug.assert(len <= self.len);

            var mapped: d3d11.D3D11_MAPPED_SUBRESOURCE = undefined;
            const hr = self.opts.context.Map(
                @ptrCast(self.buffer.ptr),
                0,
                .WRITE_DISCARD,
                0,
                &mapped,
            );
            if (com.FAILED(hr)) {
                log.err("Buffer.map failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
                return error.DirectXFailed;
            }

            const ptr: [*]T = @ptrCast(@alignCast(mapped.pData orelse {
                log.warn("Buffer.map: Map returned null pData", .{});
                self.opts.context.Unmap(@ptrCast(self.buffer.ptr), 0);
                return error.DirectXFailed;
            }));
            return ptr[0..len];
        }

        pub fn unmap(self: *Self) void {
            self.opts.context.Unmap(@ptrCast(self.buffer.ptr), 0);
        }

        /// Resize the buffer, discarding old contents.
        ///
        /// Used when the terminal grid size changes and we need
        /// a buffer of a completely different capacity.
        pub fn resize(self: *Self, new_len: usize) !void {
            if (self.buffer.srv) |srv| _ = srv.Release();
            _ = self.buffer.ptr.Release();
            const byte_size = validateAndComputeSize(self.opts, new_len);
            const buf = try createBuffer(self.opts, byte_size, null);
            const srv = try createSRV(self.opts, buf, new_len);
            self.buffer = .{ .ptr = buf, .srv = srv };
            self.len = new_len;
        }

        /// Compute byte size and validate DX11 constraints.
        ///
        /// DX11 requires constant buffer sizes to be a multiple of 16 bytes.
        /// This is because the GPU reads constant buffers in 16-byte chunks
        /// (float4-sized), and a misaligned buffer would read garbage at the end.
        fn validateAndComputeSize(opts: Options, len: usize) u32 {
            const byte_size = len * @sizeOf(T);
            if (opts.bind_flags & d3d11.D3D11_BIND_CONSTANT_BUFFER != 0) {
                if (byte_size % 16 != 0) {
                    std.debug.panic(
                        "Constant buffer size must be a multiple of 16 bytes, got {} (T={s}, len={})",
                        .{ byte_size, @typeName(T), len },
                    );
                }
            }
            return @intCast(byte_size);
        }

        fn createBuffer(
            opts: Options,
            byte_size: u32,
            initial_data: ?*const d3d11.D3D11_SUBRESOURCE_DATA,
        ) !*d3d11.ID3D11Buffer {
            const is_structured = opts.structure_byte_stride > 0;
            const desc = d3d11.D3D11_BUFFER_DESC{
                .ByteWidth = byte_size,
                .Usage = .DYNAMIC,
                .BindFlags = opts.bind_flags,
                .CPUAccessFlags = d3d11.D3D11_CPU_ACCESS_WRITE,
                .MiscFlags = if (is_structured) d3d11.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED else 0,
                .StructureByteStride = opts.structure_byte_stride,
            };
            var buffer: ?*d3d11.ID3D11Buffer = null;
            const hr = opts.device.CreateBuffer(&desc, initial_data, &buffer);
            if (com.FAILED(hr)) {
                log.err("CreateBuffer failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
                return error.DirectXFailed;
            }
            return buffer orelse error.DirectXFailed;
        }

        /// Create an SRV for structured buffers so the shader can read
        /// them as StructuredBuffer<T>. Returns null for non-structured
        /// buffers (vertex, constant).
        fn createSRV(
            opts: Options,
            buf: *d3d11.ID3D11Buffer,
            num_elements: usize,
        ) !?*d3d11.ID3D11ShaderResourceView {
            if (opts.structure_byte_stride == 0) return null;

            const desc = d3d11.D3D11_SHADER_RESOURCE_VIEW_DESC{
                .Format = .UNKNOWN,
                .ViewDimension = .BUFFER,
                // Buffer union: FirstElement = MostDetailedMip, NumElements = MipLevels
                .MostDetailedMip = 0,
                .MipLevels = @intCast(num_elements),
            };
            var srv: ?*d3d11.ID3D11ShaderResourceView = null;
            const hr = opts.device.CreateShaderResourceView(
                @ptrCast(buf),
                &desc,
                &srv,
            );
            if (com.FAILED(hr)) {
                log.err("CreateShaderResourceView for structured buffer failed: hr=0x{x}", .{@as(u32, @bitCast(hr))});
                return error.DirectXFailed;
            }
            return srv orelse error.DirectXFailed;
        }
    };
}
