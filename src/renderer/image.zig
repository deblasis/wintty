const std = @import("std");
const Allocator = std.mem.Allocator;
const assert = @import("../quirks.zig").inlineAssert;
const wuffs = @import("wuffs");
const terminal = @import("../terminal/main.zig");
const global = @import("../global.zig");

const Renderer = @import("../renderer.zig").Renderer;
const GraphicsAPI = Renderer.API;
const Texture = GraphicsAPI.Texture;
const CellSize = @import("size.zig").CellSize;
const Overlay = @import("Overlay.zig");

const log = std.log.scoped(.renderer_image);

/// Default per-frame UPLOAD-heap budget for image uploads. The State
/// loop defers any pending image whose chunked-upload total would push
/// the in-flight sum past this number; the deferred image stays in
/// .pending and retries on the next frame. 128 MiB leaves headroom for
/// two ~48 MiB images concurrent with smaller atlases/overlays without
/// pressuring Windows shared-memory.
const TEXTURE_UPLOAD_BUDGET_DEFAULT_BYTES: u64 = 128 * 1024 * 1024;

/// Mirror of directx12/Texture.zig TEXTURE_DATA_PITCH_ALIGNMENT. Kept
/// local so the budget math stays renderer-agnostic; the comptime
/// block below catches drift on DX12 builds, and on backends without
/// the constant (Metal/OpenGL) the assert is skipped.
const TEXTURE_UPLOAD_PITCH_ALIGNMENT: u64 = 256;

comptime {
    if (@hasDecl(Texture, "TEXTURE_DATA_PITCH_ALIGNMENT")) {
        std.debug.assert(
            TEXTURE_UPLOAD_PITCH_ALIGNMENT == Texture.TEXTURE_DATA_PITCH_ALIGNMENT,
        );
    }
}

/// Compute the UPLOAD-heap bytes that will be consumed when uploading an
/// image of the given dimensions to a DX12 texture. The destination is
/// always RGBA (Image.convert() swizzles gray/rgb/bgr/etc. to 4 bpp
/// before upload), and Texture.uploadRegion aligns each row to
/// TEXTURE_UPLOAD_PITCH_ALIGNMENT (256 bytes). Pure function so
/// State.upload can size-check without touching the DX12 layer; the
/// arithmetic is u64 throughout to keep multi-GB hypotheticals from
/// silently wrapping the u32 intermediate.
fn imageStagingBytes(width: u32, height: u32) u64 {
    const row_bytes: u64 = @as(u64, width) * 4;
    const align_mask: u64 = TEXTURE_UPLOAD_PITCH_ALIGNMENT - 1;
    const aligned: u64 = (row_bytes + align_mask) & ~align_mask;
    return aligned * @as(u64, height);
}

/// Budget boundary check: does adding `new_est` bytes to the current
/// `in_flight` total push past `budget`? Strict greater-than so the
/// budget is the exact ceiling (in_flight + est == budget is allowed).
fn wouldExceedBudget(in_flight: u64, new_est: u64, budget: u64) bool {
    return in_flight + new_est > budget;
}

/// Generic image rendering state for the renderer. This stores all
/// images and their placements and exposes only a limited public API
/// for adding images and placements and drawing them.
pub const State = struct {
    /// The full image state for the renderer that specifies what images
    /// need to be uploaded, pruned, etc.
    images: ImageMap,

    /// The placements for the Kitty image protocol.
    kitty_placements: std.ArrayListUnmanaged(Placement),

    /// The end index (exclusive) for placements that should be
    /// drawn below the background, below the text, etc.
    kitty_bg_end: u32,
    kitty_text_end: u32,

    /// True if there are any virtual placements. This needs to be known
    /// because virtual placements need to be recalculated more often
    /// on frame builds and are generally more expensive to handle.
    kitty_virtual: bool,

    /// Overlays
    overlay_placements: std.ArrayListUnmanaged(Placement),

    /// Per-frame UPLOAD-heap budget for image uploads. State.upload
    /// defers any pending image whose chunked upload would push the
    /// running in-flight total past this. Tunable per-State instance;
    /// defaults to TEXTURE_UPLOAD_BUDGET_DEFAULT_BYTES (128 MiB).
    upload_budget_bytes: u64 = TEXTURE_UPLOAD_BUDGET_DEFAULT_BYTES,

    pub const empty: State = .{
        .images = .empty,
        .kitty_placements = .empty,
        .kitty_bg_end = 0,
        .kitty_text_end = 0,
        .kitty_virtual = false,
        .overlay_placements = .empty,
        .upload_budget_bytes = TEXTURE_UPLOAD_BUDGET_DEFAULT_BYTES,
    };

    pub fn deinit(self: *State, alloc: Allocator) void {
        {
            var it = self.images.iterator();
            while (it.next()) |kv| kv.value_ptr.image.deinit(alloc);
            self.images.deinit(alloc);
        }
        self.kitty_placements.deinit(alloc);
        self.overlay_placements.deinit(alloc);
    }

    /// Upload any images to the GPU that need to be uploaded,
    /// and remove any images that are no longer needed on the GPU.
    ///
    /// A failed upload (e.g. wuffs decode error, DX12 UploadFailed)
    /// flips the image to its unload state so the next iteration
    /// sweeps it. Without this, a persistently-failing image
    /// retries every frame and floods the log; the placement
    /// can't render anyway, so dropping it is the correct outcome.
    ///
    /// Returns true if every pending image uploaded successfully,
    /// false if any failed.
    pub fn upload(
        self: *State,
        alloc: Allocator,
        api: *GraphicsAPI,
    ) bool {
        // Backends whose Texture tracks staging-heap pressure (DX12) run
        // the budget gate; everything else falls through to a straight
        // upload loop. The flag is comptime so dead branches drop out.
        const budgeted = comptime @hasField(Texture, "pending_staging_bytes");

        // Pass 1: sweep unloads + accumulate in-flight from survivors.
        // Done first so deferral checks in pass 2 see the post-unload
        // total (an unloaded image's staging is about to be Released).
        var bytes_in_flight: u64 = 0;
        {
            var image_it = self.images.iterator();
            while (image_it.next()) |kv| {
                const img = &kv.value_ptr.image;
                if (img.isUnloading()) {
                    img.deinit(alloc);
                    self.images.removeByPtr(kv.key_ptr);
                    continue;
                }
                if (budgeted) bytes_in_flight += img.pendingStagingBytes();
            }
        }

        // Pass 2: upload pending images, gating on DX12's budget.
        var success: bool = true;
        var image_it = self.images.iterator();
        while (image_it.next()) |kv| {
            const img = &kv.value_ptr.image;
            if (!img.isPending()) continue;

            const est: u64 = if (budgeted) img.estimatedUploadStagingBytes() else 0;
            // upload_budget_bytes == 0 means "unbounded" per Config.zig
            // documentation; the user-facing knob would otherwise stall every
            // upload if literally zero, which is a foot-gun rather than a
            // useful semantic (compare image-storage-limit = 0 disabling
            // image protocols entirely).
            if (budgeted and self.upload_budget_bytes != 0 and
                wouldExceedBudget(bytes_in_flight, est, self.upload_budget_bytes))
            {
                log.debug(
                    "deferring image upload est={d} in_flight={d} budget={d}",
                    .{ est, bytes_in_flight, self.upload_budget_bytes },
                );
                continue;
            }

            img.upload(
                alloc,
                api,
            ) catch |err| {
                log.warn(
                    "error uploading image to GPU err={t}, dropping placement",
                    .{err},
                );
                // markForUnload moves the image to .unload_pending whose
                // pendingStagingBytes() returns 0, so next frame's in-flight
                // total drops by `est` naturally. No manual decrement needed.
                img.markForUnload();
                success = false;
                continue;
            };
            if (budgeted) bytes_in_flight += est;
        }

        return success;
    }

    pub const DrawPlacements = enum {
        kitty_below_bg,
        kitty_below_text,
        kitty_above_text,
        overlay,
    };

    /// Draw the given named set of placements.
    ///
    /// Any placements that have non-uploaded images are ignored. Any
    /// graphics API errors during drawing are also ignored.
    ///
    /// `frame_buffers` is owned by the caller; each appended buffer
    /// must outlive the GPU's read of it. On DX12, IASetVertexBuffers
    /// records only the GPU virtual address, so freeing the underlying
    /// ID3D12Resource before the command list executes makes the GPU
    /// read zeros. The frame state retains buffers until its semaphore
    /// confirms the previous frame is done.
    pub fn draw(
        self: *State,
        alloc: Allocator,
        api: *GraphicsAPI,
        pipeline: GraphicsAPI.Pipeline,
        pass: *GraphicsAPI.RenderPass,
        placement_type: DrawPlacements,
        uniforms: anytype,
        frame_buffers: *std.ArrayListUnmanaged(GraphicsAPI.Buffer(GraphicsAPI.shaders.Image)),
    ) void {
        const placements: []const Placement = switch (placement_type) {
            .kitty_below_bg => self.kitty_placements.items[0..self.kitty_bg_end],
            .kitty_below_text => self.kitty_placements.items[self.kitty_bg_end..self.kitty_text_end],
            .kitty_above_text => self.kitty_placements.items[self.kitty_text_end..],
            .overlay => self.overlay_placements.items,
        };

        for (placements) |p| {
            // Look up the image
            const image = self.images.get(p.image_id) orelse {
                log.warn("image not found for placement image_id={}", .{p.image_id});
                continue;
            };

            // Get the texture
            const texture = switch (image.image) {
                .ready,
                .unload_ready,
                => |t| t,
                else => {
                    log.warn("image not ready for placement image_id={}", .{p.image_id});
                    continue;
                },
            };

            // Reserve the slot first so we can build the buffer directly
            // into the caller-owned retention list; avoids the
            // append-then-handle-OOM dance that would otherwise need to
            // call deinit on a buffer the GPU may already be reading.
            frame_buffers.ensureUnusedCapacity(alloc, 1) catch |err| {
                log.warn("error reserving image vertex buffer slot err={}", .{err});
                continue;
            };

            // Create our vertex buffer, which is always exactly one item.
            // future(mitchellh): we can group rendering multiple instances of a single image
            const buf = GraphicsAPI.Buffer(GraphicsAPI.shaders.Image).initFill(
                api.imageBufferOptions(),
                &.{.{
                    .grid_pos = .{
                        @as(f32, @floatFromInt(p.x)),
                        @as(f32, @floatFromInt(p.y)),
                    },

                    .cell_offset = .{
                        @as(f32, @floatFromInt(p.cell_offset_x)),
                        @as(f32, @floatFromInt(p.cell_offset_y)),
                    },

                    .source_rect = .{
                        @as(f32, @floatFromInt(p.source_x)),
                        @as(f32, @floatFromInt(p.source_y)),
                        @as(f32, @floatFromInt(p.source_width)),
                        @as(f32, @floatFromInt(p.source_height)),
                    },

                    .dest_size = .{
                        @as(f32, @floatFromInt(p.width)),
                        @as(f32, @floatFromInt(p.height)),
                    },
                }},
            ) catch |err| {
                log.warn("error creating image vertex buffer err={}", .{err});
                continue;
            };
            frame_buffers.appendAssumeCapacity(buf);

            pass.step(.{
                .pipeline = pipeline,
                .uniforms = uniforms,
                .buffers = &.{frame_buffers.items[frame_buffers.items.len - 1].buffer},
                .textures = &.{texture},
                .draw = .{
                    .type = .triangle_strip,
                    .vertex_count = 4,
                },
            });
        }
    }

    /// Update our overlay state. Null value deletes any existing overlay.
    pub fn overlayUpdate(
        self: *State,
        alloc: Allocator,
        overlay_: ?Overlay,
    ) !void {
        const overlay = overlay_ orelse {
            // If we don't have an overlay, remove any existing one.
            if (self.images.getPtr(.overlay)) |data| {
                data.image.markForUnload();
            }
            return;
        };

        // Overlays are always considered new content, so we take a
        // fresh generation stamp to force replacing any existing one.
        const generation = terminal.kitty.graphics.nextGeneration(global.io());

        // Ensure we have space for our overlay placement. Do this before
        // we upload our image so we don't have to deal with cleaning
        // that up.
        self.overlay_placements.clearRetainingCapacity();
        try self.overlay_placements.ensureUnusedCapacity(alloc, 1);

        // Setup our image.
        const pending = overlay.pendingImage();
        try self.prepImage(
            alloc,
            .overlay,
            generation,
            pending,
        );
        errdefer comptime unreachable;

        // Setup our placement
        self.overlay_placements.appendAssumeCapacity(.{
            .image_id = .overlay,
            .x = 0,
            .y = 0,
            .z = 0,
            .width = pending.width,
            .height = pending.height,
            .cell_offset_x = 0,
            .cell_offset_y = 0,
            .source_x = 0,
            .source_y = 0,
            .source_width = pending.width,
            .source_height = pending.height,
        });
    }

    /// Returns true if the Kitty graphics state requires an update based
    /// on the terminal state and our internal state.
    ///
    /// This does not read/write state used by drawing.
    pub fn kittyRequiresUpdate(
        self: *const State,
        t: *const terminal.Terminal,
    ) bool {
        // If the terminal kitty image state is dirty, we must update.
        if (t.screens.active.kitty_images.dirty) return true;

        // If we have any virtual references, we must also rebuild our
        // kitty state on every frame because any cell change can move
        // an image. If the virtual placements were removed, this will
        // be set to false on the next update.
        if (self.kitty_virtual) return true;

        return false;
    }

    /// Update the Kitty graphics state from the terminal.
    ///
    /// This reads/writes state used by drawing.
    pub fn kittyUpdate(
        self: *State,
        alloc: Allocator,
        t: *const terminal.Terminal,
        cell_size: CellSize,
    ) void {
        const storage = &t.screens.active.kitty_images;
        defer storage.dirty = false;

        // We always clear our previous placements no matter what because
        // we rebuild them from scratch.
        self.kitty_placements.clearRetainingCapacity();
        self.kitty_virtual = false;

        // Go through our known images and if there are any that are no longer
        // in use then mark them to be freed.
        //
        // This never conflicts with the below because a placement can't
        // reference an image that doesn't exist.
        {
            var it = self.images.iterator();
            while (it.next()) |kv| {
                switch (kv.key_ptr.*) {
                    // We're only looking at Kitty images
                    .kitty => |id| if (storage.imageById(id)) |image| {
                        // A pending source must not keep an older texture for
                        // the same ID renderable while its placements wait.
                        if (image.data.isPending()) {
                            kv.value_ptr.image.markForUnload();
                        }
                    } else {
                        kv.value_ptr.image.markForUnload();
                    },

                    .overlay => {},
                }
            }
        }

        // The top-left and bottom-right corners of our viewport in screen
        // points. This lets us determine offsets and containment of placements.
        const top = t.screens.active.pages.getTopLeft(.viewport);
        const bot = t.screens.active.pages.getBottomRight(.viewport).?;
        const top_y = t.screens.active.pages.pointFromPin(.screen, top).?.screen.y;
        const bot_y = t.screens.active.pages.pointFromPin(.screen, bot).?.screen.y;

        // Relative placements whose parent chain roots at a virtual
        // placement can only be positioned once the placeholder cells
        // have been scanned below, so they are collected here first.
        var pending_relative: std.ArrayListUnmanaged(struct {
            image_id: u32,
            p: terminal.kitty.graphics.ImageStorage.Placement,
            root_key: terminal.kitty.graphics.ImageStorage.PlacementKey,
            horizontal_offset: i32,
            vertical_offset: i32,
        }) = .empty;
        defer pending_relative.deinit(alloc);

        // Go through the placements and ensure the image is
        // on the GPU or else is ready to be sent to the GPU.
        var it = storage.placements.iterator();
        while (it.next()) |kv| {
            const p = kv.value_ptr;

            // Special logic based on location
            const origin: Origin = switch (p.location) {
                .pin => |pin| .{ .pin = pin },

                .virtual => {
                    // We need to mark virtual placements on our renderer so that
                    // we know to rebuild in more scenarios since cell changes can
                    // now trigger placement changes.
                    self.kitty_virtual = true;

                    // We also continue out because virtual placements are
                    // only triggered by the unicode placeholder, not by the
                    // placement itself.
                    continue;
                },

                .relative => |rel| origin: {
                    // An unresolvable chain is never drawn. This only
                    // happens transiently (storage reaps orphans) or for
                    // chains re-parented too deep, which kitty doesn't
                    // draw either.
                    const chain = storage.resolveChain(rel) orelse continue;
                    switch (chain.root.location) {
                        // Rooted at a pin: anchored at the root's pin,
                        // offset by the accumulated chain offsets.
                        .pin => |root_pin| break :origin .{
                            .pin = root_pin,
                            .horizontal_offset = chain.horizontal_offset,
                            .vertical_offset = chain.vertical_offset,
                        },

                        // Rooted at a virtual placement: positioned from
                        // the root's placeholder cells, which we only
                        // know after the placeholder scan below. The
                        // placeholders also move with cell changes so we
                        // must rebuild every frame, like virtuals.
                        .virtual => {
                            self.kitty_virtual = true;
                            pending_relative.append(alloc, .{
                                .image_id = kv.key_ptr.image_id,
                                .p = p.*,
                                .root_key = chain.root_key,
                                .horizontal_offset = chain.horizontal_offset,
                                .vertical_offset = chain.vertical_offset,
                            }) catch |err| {
                                log.warn("error deferring relative placement err={}", .{err});
                            };
                            continue;
                        },

                        // resolveChain roots are never relative.
                        .relative => unreachable,
                    }
                },
            };

            // Get the image for the placement
            const image = storage.imageById(kv.key_ptr.image_id) orelse {
                log.warn(
                    "missing image for placement, ignoring image_id={}",
                    .{kv.key_ptr.image_id},
                );
                continue;
            };

            self.prepKittyPlacement(
                alloc,
                t,
                top_y,
                bot_y,
                &image,
                p,
                origin,
            ) catch |err| {
                // For errors we log and continue. We try to place
                // other placements even if one fails.
                log.warn("error preparing kitty placement err={}", .{err});
            };
        }

        // If we have virtual placements then we need to scan for placeholders.
        if (self.kitty_virtual) {
            // The minimum placeholder cell seen per virtual placement,
            // in viewport coordinates. This is the origin for relative
            // placements rooted at a virtual placement: kitty positions
            // those at the min-x/min-y of the parent's placeholder
            // cells. Only tracked when such placements exist.
            var virtual_origins: std.AutoHashMapUnmanaged(
                terminal.kitty.graphics.ImageStorage.PlacementKey,
                struct { x: u32, y: u32 },
            ) = .empty;
            defer virtual_origins.deinit(alloc);

            var v_it = terminal.kitty.graphics.unicode.placementIterator(top, bot);
            while (v_it.next()) |virtual_p| {
                self.prepKittyVirtualPlacement(
                    alloc,
                    t,
                    &virtual_p,
                    cell_size,
                ) catch |err| {
                    // For errors we log and continue. We try to place
                    // other placements even if one fails.
                    log.warn("error preparing kitty placement err={}", .{err});
                };

                // We need to track the origins of all the placeholders
                // when we have relative cells so that we can calculate
                // the proper offsets later.
                if (pending_relative.items.len > 0) fold: {
                    // Find the target for this virtual placeholder.
                    const target = storage.placeholderTarget(
                        virtual_p.image_id,
                        virtual_p.placement_id,
                    ) orelse break :fold;

                    // Get the actual viewport position for it.
                    const vp = t.screens.active.pages.pointFromPin(
                        .viewport,
                        virtual_p.pin,
                    ) orelse break :fold;

                    // Add the origin for this target
                    const gop = virtual_origins.getOrPut(
                        alloc,
                        target.key,
                    ) catch |err| {
                        log.warn("error tracking virtual origin err={}", .{err});
                        break :fold;
                    };
                    if (!gop.found_existing) {
                        gop.value_ptr.* = .{ .x = vp.viewport.x, .y = vp.viewport.y };
                    } else {
                        gop.value_ptr.x = @min(gop.value_ptr.x, vp.viewport.x);
                        gop.value_ptr.y = @min(gop.value_ptr.y, vp.viewport.y);
                    }
                }
            }

            // Position the relative placements rooted at virtual
            // placements now that the placeholder cells are known. A
            // root with no placeholders on screen leaves its relative
            // placements undrawn, matching kitty.
            for (pending_relative.items) |pr| {
                const origin = virtual_origins.get(pr.root_key) orelse continue;
                const image = storage.imageById(pr.image_id) orelse continue;
                if (image.data.isPending()) continue;

                const grid = pr.p.gridSize(image, t);
                if (grid.cols == 0 or grid.rows == 0) continue;

                // Viewport-relative signed position; cull placements
                // entirely outside the viewport.
                const x: i64 = @as(i64, origin.x) + pr.horizontal_offset;
                const y: i64 = @as(i64, origin.y) + pr.vertical_offset;
                if (y >= t.rows or y + grid.rows - 1 < 0) continue;
                if (x >= t.cols or x + grid.cols - 1 < 0) continue;

                self.appendKittyPlacement(
                    alloc,
                    t,
                    &image,
                    &pr.p,
                    std.math.cast(i32, x) orelse continue,
                    std.math.cast(i32, y) orelse continue,
                ) catch |err| {
                    log.warn("error preparing kitty placement err={}", .{err});
                };
            }
        }

        // Sort the placements by their Z value.
        std.mem.sortUnstable(
            Placement,
            self.kitty_placements.items,
            {},
            struct {
                fn lessThan(
                    ctx: void,
                    lhs: Placement,
                    rhs: Placement,
                ) bool {
                    _ = ctx;
                    return lhs.z < rhs.z or
                        (lhs.z == rhs.z and lhs.image_id.zLessThan(rhs.image_id));
                }
            }.lessThan,
        );

        // Find our indices. The values are sorted by z so we can
        // find the first placement out of bounds to find the limits.
        const bg_limit = std.math.minInt(i32) / 2;
        var bg_end: ?u32 = null;
        var text_end: ?u32 = null;
        for (self.kitty_placements.items, 0..) |p, i| {
            if (bg_end == null and p.z >= bg_limit) bg_end = @intCast(i);
            if (text_end == null and p.z >= 0) text_end = @intCast(i);
        }

        // If we didn't see any images with a z > the bg limit,
        // then our bg end is the end of our placement list.
        self.kitty_bg_end =
            bg_end orelse @intCast(self.kitty_placements.items.len);
        // Same idea for the image_text_end.
        self.kitty_text_end =
            text_end orelse @intCast(self.kitty_placements.items.len);
    }

    const PrepImageError = error{
        OutOfMemory,
        ImageConversionError,
    };

    /// Where a placement is anchored on screen: a pin, plus cell
    /// offsets from it for relative placements. The pin is the
    /// placement's own pin for pin placements; for relative placements
    /// it is the root of the parent chain and the offsets are the
    /// accumulated chain offsets in cells.
    const Origin = struct {
        pin: *const terminal.Pin,
        horizontal_offset: i32 = 0,
        vertical_offset: i32 = 0,
    };

    /// Get the viewport-relative position for this placement and add it
    /// to the placements list.
    fn prepKittyPlacement(
        self: *State,
        alloc: Allocator,
        t: *const terminal.Terminal,
        top_y: u32,
        bot_y: u32,
        image: *const terminal.kitty.graphics.Image,
        p: *const terminal.kitty.graphics.ImageStorage.Placement,
        origin: Origin,
    ) PrepImageError!void {
        // Keep the native placement but do not create a renderer placement or
        // texture until the decoded bytes arrive.
        if (image.data.isPending()) return;

        // An origin whose tracked content was pruned has no position.
        if (origin.pin.garbage) return;

        // The size of the placement in grid cells. A zero size can
        // occur when pixel geometry is unavailable; nothing to place.
        const grid = p.gridSize(image.*, t);
        if (grid.cols == 0 or grid.rows == 0) return;

        // This is expensive but necessary.
        const origin_y = t.screens.active.pages.pointFromPin(
            .screen,
            origin.pin.*,
        ).?.screen.y;

        // The placement's edges in screen coordinates. Chain offsets
        // are signed: a relative placement can hang above or to the
        // left of its origin, so this math must be signed and widened.
        const img_top_y: i64 = @as(i64, origin_y) + origin.vertical_offset;
        const img_bot_y: i64 = img_top_y + grid.rows - 1;
        const img_left_x: i64 = @as(i64, origin.pin.x) + origin.horizontal_offset;
        const img_right_x: i64 = img_left_x + grid.cols - 1;

        // If the placement isn't within our viewport then skip it.
        if (img_top_y > bot_y or img_bot_y < top_y) return;
        if (img_left_x >= t.cols or img_right_x < 0) return;

        // Viewport-relative position. Offsets so extreme that the
        // position is unrepresentable have no renderable pixels.
        const y_pos = std.math.cast(i32, img_top_y - top_y) orelse return;
        const x_pos = std.math.cast(i32, img_left_x) orelse return;

        try self.appendKittyPlacement(alloc, t, image, p, x_pos, y_pos);
    }

    /// Compute the sizes for a native kitty placement positioned at the
    /// given viewport cell position, prepare its image for the GPU, and
    /// append it to the placements list.
    fn appendKittyPlacement(
        self: *State,
        alloc: Allocator,
        t: *const terminal.Terminal,
        image: *const terminal.kitty.graphics.Image,
        p: *const terminal.kitty.graphics.ImageStorage.Placement,
        x: i32,
        y: i32,
    ) PrepImageError!void {
        // We need to prep this image for upload if it isn't in the
        // cache OR it is in the cache but the transmit time doesn't
        // match meaning this image is different.
        try self.prepKittyImage(alloc, image);

        // Calculate the dimensions of our image, taking in to
        // account the rows / columns specified by the placement.
        const dest_size = p.pixelSize(image.*, t);
        const cell_offset = p.cellOffset(t);

        const source = p.sourceRect(image.*);

        // Accumulate the placement
        if (dest_size.width > 0 and dest_size.height > 0) {
            try self.kitty_placements.append(alloc, .{
                .image_id = .{ .kitty = image.id },
                .x = x,
                .y = y,
                .z = p.z,
                .width = dest_size.width,
                .height = dest_size.height,
                .cell_offset_x = cell_offset.x,
                .cell_offset_y = cell_offset.y,
                .source_x = source.x,
                .source_y = source.y,
                .source_width = source.width,
                .source_height = source.height,
            });
        }
    }

    fn prepKittyVirtualPlacement(
        self: *State,
        alloc: Allocator,
        t: *const terminal.Terminal,
        p: *const terminal.kitty.graphics.unicode.Placement,
        cell_size: CellSize,
    ) PrepImageError!void {
        const storage = &t.screens.active.kitty_images;
        const image = storage.imageById(p.image_id) orelse {
            log.warn(
                "missing image for virtual placement, ignoring image_id={}",
                .{p.image_id},
            );
            return;
        };
        if (image.data.isPending()) return;

        const rp = p.renderPlacement(
            storage,
            &image,
            cell_size.width,
            cell_size.height,
        ) catch |err| {
            log.warn("error rendering virtual placement err={}", .{err});
            return;
        };

        // If our placement is zero sized then we don't do anything.
        if (rp.dest_width == 0 or rp.dest_height == 0) return;

        const viewport: terminal.point.Point = t.screens.active.pages.pointFromPin(
            .viewport,
            rp.top_left,
        ) orelse {
            // This is unreachable with virtual placements because we should
            // only ever be looking at virtual placements that are in our
            // viewport in the renderer and virtual placements only ever take
            // up one row.
            unreachable;
        };

        // Prepare the image for the GPU and store the placement.
        try self.prepKittyImage(alloc, &image);
        try self.kitty_placements.append(alloc, .{
            .image_id = .{ .kitty = image.id },
            .x = @intCast(rp.top_left.x),
            .y = @intCast(viewport.viewport.y),
            .z = -1,
            .width = rp.dest_width,
            .height = rp.dest_height,
            .cell_offset_x = rp.offset_x,
            .cell_offset_y = rp.offset_y,
            .source_x = rp.source_x,
            .source_y = rp.source_y,
            .source_width = rp.source_width,
            .source_height = rp.source_height,
        });
    }

    /// Prepare an image for upload to the GPU.
    fn prepImage(
        self: *State,
        alloc: Allocator,
        id: Id,
        generation: u64,
        pending: Image.Pending,
    ) PrepImageError!void {
        // If this image exists and its generation is the same it is the
        // identical image so we don't need to send it to the GPU.
        const gop = try self.images.getOrPut(alloc, id);
        if (gop.found_existing and
            gop.value_ptr.generation == generation)
        {
            return;
        }

        // Store it in the map. The largest decoded image we expect here is
        // bounded by the kitty graphics APC payload cap (also used by iTerm2
        // multipart File= after the multipart series), currently 65 MiB. DX12
        // uploads are chunked into row-bands so the GPU staging-heap pressure
        // scales with band size, not full-image size.
        const new_image: Image = if (pending.convertCopy(alloc)) |v| v else |_| {
            if (!gop.found_existing) {
                // If this is a new entry we can just remove it since it
                // was never sent to the GPU.
                _ = self.images.remove(id);
            } else {
                // If this was an existing entry, it is invalid and
                // we must unload it.
                gop.value_ptr.image.markForUnload();
            }

            return error.OutOfMemory;
        };
        // Note: we don't need to errdefer free the data because it is
        // put into the map immediately below and our errdefer to
        // handle our map state will fix this up.

        if (!gop.found_existing) {
            gop.value_ptr.* = .{
                .image = new_image,
                .generation = 0,
            };
        } else {
            gop.value_ptr.image.markForReplace(
                alloc,
                new_image,
            );
        }

        // If any error happens, we unload the image and it is invalid.
        errdefer gop.value_ptr.image.markForUnload();

        gop.value_ptr.image.getPendingPointer().?.prepForUpload(alloc) catch |err| {
            log.warn("error preparing image for upload err={}", .{err});
            return error.ImageConversionError;
        };
        gop.value_ptr.generation = generation;
    }

    /// Prepare the provided Kitty image for upload to the GPU by copying its
    /// data with our allocator and setting it to the pending state.
    fn prepKittyImage(
        self: *State,
        alloc: Allocator,
        image: *const terminal.kitty.graphics.Image,
    ) PrepImageError!void {
        // For animated images this is the current animation frame;
        // the image generation changes whenever the current frame
        // does, so the upload cache stays coherent.
        const data = image.renderData().bytes() orelse unreachable;
        try self.prepImage(
            alloc,
            .{ .kitty = image.id },
            image.generation,
            .{
                .width = image.width,
                .height = image.height,
                .pixel_format = switch (image.format) {
                    .gray => .gray,
                    .gray_alpha => .gray_alpha,
                    .rgb => .rgb,
                    .rgba => .rgba,
                    // PNG/JPEG/GIF arrive at the renderer only after
                    // the image decoder has rewritten the format to
                    // .rgba.
                    .png, .jpeg, .gif => unreachable,
                },

                // constCasts are always gross but this one is safe is because
                // the data is only read from here and copied into its own
                // buffer.
                .data = @constCast(data.ptr),
            },
        );
    }
};

/// Represents a single image placement on the grid.
/// A placement is a request to render an instance of an image.
pub const Placement = struct {
    /// The image being rendered. This MUST be in the image map.
    image_id: Id,

    /// The grid x/y where this placement is located.
    x: i32,
    y: i32,
    z: i32,

    /// The width/height of the placed image.
    width: u32,
    height: u32,

    /// The offset in pixels from the top left of the cell.
    /// This is clamped to the size of a cell.
    cell_offset_x: u32,
    cell_offset_y: u32,

    /// The source rectangle of the placement.
    source_x: u32,
    source_y: u32,
    source_width: u32,
    source_height: u32,
};

/// Image identifier used to store and lookup images.
///
/// This is tagged by different image types to make it easier to
/// store different kinds of images in the same map without having
/// to worry about ID collisions.
pub const Id = union(enum) {
    /// Image sent to the terminal state via the kitty graphics protocol.
    /// The value is the ID assigned by the terminal.
    kitty: u32,

    /// Debug overlay. This is always composited down to a single
    /// image for now. In the future we can support layers here if we want.
    overlay,

    /// Z-ordering tie-breaker for images with the same z value.
    pub fn zLessThan(lhs: Id, rhs: Id) bool {
        // If our tags aren't the same, we sort by tag.
        if (std.meta.activeTag(lhs) != std.meta.activeTag(rhs)) {
            return switch (lhs) {
                // Kitty images always sort before (lower z) non-kitty images.
                .kitty => true,

                .overlay => false,
            };
        }

        switch (lhs) {
            .kitty => |lhs_id| {
                const rhs_id = rhs.kitty;
                return lhs_id < rhs_id;
            },

            // No sensical ordering
            .overlay => return false,
        }
    }
};

/// The map used for storing images.
pub const ImageMap = std.AutoHashMapUnmanaged(Id, struct {
    image: Image,

    /// The generation of the terminal image this was created from
    /// (see terminal.kitty.graphics.Image.generation). Used to detect
    /// staleness: a differing generation for the same ID means the
    /// contents changed and the texture must be replaced. Zero is
    /// never a valid stored generation so it marks "not yet uploaded".
    generation: u64,
});

/// The state for a single image that is to be rendered.
pub const Image = union(enum) {
    /// The image data is pending upload to the GPU.
    ///
    /// This data is owned by this union so it must be freed once uploaded.
    pending: Pending,

    /// This is the same as the pending states but there is
    /// a texture already allocated that we want to replace.
    replace: Replace,

    /// The image is uploaded and ready to be used.
    ready: Texture,

    /// The image isn't uploaded yet but is scheduled to be unloaded.
    unload_pending: Pending,
    /// The image is uploaded and is scheduled to be unloaded.
    unload_ready: Texture,
    /// The image is uploaded and scheduled to be replaced
    /// with new data, but it's also scheduled to be unloaded.
    unload_replace: Replace,

    pub const Replace = struct {
        texture: Texture,
        pending: Pending,
    };

    /// Pending image data that needs to be uploaded to the GPU.
    pub const Pending = struct {
        height: u32,
        width: u32,
        pixel_format: PixelFormat,

        /// Data is always expected to be (width * height * bpp).
        data: [*]u8,

        pub fn dataSlice(self: Pending) []u8 {
            return self.data[0..self.len()];
        }

        pub fn len(self: Pending) usize {
            return self.width * self.height * self.pixel_format.bpp();
        }

        pub const PixelFormat = enum {
            /// 1 byte per pixel grayscale.
            gray,
            /// 2 bytes per pixel grayscale + alpha.
            gray_alpha,
            /// 3 bytes per pixel RGB.
            rgb,
            /// 3 bytes per pixel BGR.
            bgr,
            /// 4 byte per pixel RGBA.
            rgba,
            /// 4 byte per pixel BGRA.
            bgra,

            /// Get bytes per pixel for this format.
            pub inline fn bpp(self: PixelFormat) usize {
                return switch (self) {
                    .gray => 1,
                    .gray_alpha => 2,
                    .rgb => 3,
                    .bgr => 3,
                    .rgba => 4,
                    .bgra => 4,
                };
            }
        };

        /// Converts the image data and replaces it with a format that can be uploaded to the GPU.
        /// If the data is already in a format that can be uploaded, this is a
        /// no-op.
        /// Use `convertCopy()` to convert and copy in a single-pass, such as for owning the data to upload.
        fn convertReplace(self: *Image.Pending, alloc: Allocator) wuffs.Error!void {
            // As things stand, we currently convert all images to RGBA before
            // uploading to the GPU. This just makes things easier. In the future
            // we may want to support other formats.
            if (self.pixel_format == .rgba) return;
            // If the pending data isn't RGBA we'll need to swizzle it.
            const data = self.dataSlice();
            const rgba = try switch (self.pixel_format) {
                .gray => wuffs.swizzle.gToRgba(alloc, data),
                .gray_alpha => wuffs.swizzle.gaToRgba(alloc, data),
                .rgb => wuffs.swizzle.rgbToRgba(alloc, data),
                .bgr => wuffs.swizzle.bgrToRgba(alloc, data),
                .rgba => unreachable,
                .bgra => wuffs.swizzle.bgraToRgba(alloc, data),
            };
            alloc.free(data);
            self.data = rgba.ptr;
            self.pixel_format = .rgba;
        }

        /// Converts the image data to a copy with a format that can be uploaded to the GPU.
        /// If the data is already owned, use `convertReplace()` to be a no-op for data already
        /// in a format that can be uploaded.
        fn convertCopy(self: *const Image.Pending, alloc: Allocator) wuffs.Error!Image {
            // As things stand, we currently convert all images to RGBA before
            // uploading to the GPU. This just makes things easier. In the future
            // we may want to support other formats.
            const data = self.dataSlice();
            const rgba = try switch (self.pixel_format) {
                .gray => wuffs.swizzle.gToRgba(alloc, data),
                .gray_alpha => wuffs.swizzle.gaToRgba(alloc, data),
                .rgb => wuffs.swizzle.rgbToRgba(alloc, data),
                .bgr => wuffs.swizzle.bgrToRgba(alloc, data),
                .rgba => alloc.dupe(u8, data),
                .bgra => wuffs.swizzle.bgraToRgba(alloc, data),
            };
            const result: Image = .{ .pending = .{
                .height = self.height,
                .width = self.width,
                .pixel_format = .rgba,
                .data = rgba.ptr,
            } };
            return result;
        }

        /// Prepare the pending image data for upload to the GPU.
        /// This doesn't need GPU access so is safe to call any time.
        fn prepForUpload(self: *Image.Pending, alloc: Allocator) wuffs.Error!void {
            try self.convertReplace(alloc);
        }
    };

    pub fn deinit(self: Image, alloc: Allocator) void {
        switch (self) {
            .pending,
            .unload_pending,
            => |p| alloc.free(p.dataSlice()),

            .replace, .unload_replace => |r| {
                alloc.free(r.pending.dataSlice());
                r.texture.deinit();
            },

            .ready,
            .unload_ready,
            => |t| t.deinit(),
        }
    }

    /// UPLOAD-heap bytes this image's currently-held texture is holding
    /// alive in `pending_staging`. Returns 0 on backends whose Texture
    /// doesn't track staging-heap pressure (Metal, OpenGL); only DX12
    /// keeps per-call staging buffers around for fence-gated release.
    /// The field-access switch is wrapped in a comptime `if` so the
    /// Metal/OpenGL builds never see `t.pending_staging_bytes` -- a
    /// post-return version would still type-check the unreachable body.
    pub fn pendingStagingBytes(self: Image) u64 {
        if (comptime @hasField(Texture, "pending_staging_bytes")) {
            return switch (self) {
                .pending, .unload_pending => 0,
                .ready, .unload_ready => |t| t.pending_staging_bytes,
                .replace, .unload_replace => |r| r.texture.pending_staging_bytes,
            };
        }
        return 0;
    }

    /// UPLOAD-heap bytes the next `upload()` call will require for this
    /// image. Returns 0 for already-uploaded or unload-marked variants
    /// because they won't trigger a new staging allocation, and 0 on
    /// backends without staging-heap tracking.
    pub fn estimatedUploadStagingBytes(self: Image) u64 {
        if (comptime @hasField(Texture, "pending_staging_bytes")) {
            return switch (self) {
                .pending => |p| imageStagingBytes(p.width, p.height),
                .replace => |r| imageStagingBytes(r.pending.width, r.pending.height),
                .ready,
                .unload_ready,
                .unload_pending,
                .unload_replace,
                => 0,
            };
        }
        return 0;
    }

    /// Mark this image for unload whatever state it is in.
    pub fn markForUnload(self: *Image) void {
        self.* = switch (self.*) {
            .unload_pending,
            .unload_replace,
            .unload_ready,
            => return,

            .ready => |t| .{ .unload_ready = t },
            .pending => |p| .{ .unload_pending = p },
            .replace => |r| .{ .unload_replace = r },
        };
    }

    /// Mark the current image to be replaced with a pending one. This will
    /// attempt to update the existing texture if we have one, otherwise it
    /// will act like a new upload.
    pub fn markForReplace(self: *Image, alloc: Allocator, img: Image) void {
        assert(img.isPending());

        // If we have pending data right now, free it.
        if (self.getPending()) |p| {
            alloc.free(p.dataSlice());
        }
        // If we have an existing texture, use it in the replace.
        if (self.getTexture()) |t| {
            self.* = .{ .replace = .{
                .texture = t,
                .pending = img.getPending().?,
            } };
            return;
        }
        // Otherwise we just become a pending image.
        self.* = .{ .pending = img.getPending().? };
    }

    /// Returns true if this image is pending upload.
    pub fn isPending(self: Image) bool {
        return self.getPending() != null;
    }

    /// Returns true if this image has an associated texture.
    pub fn hasTexture(self: Image) bool {
        return self.getTexture() != null;
    }

    /// Returns true if this image is marked for unload.
    pub fn isUnloading(self: Image) bool {
        return switch (self) {
            .unload_pending,
            .unload_replace,
            .unload_ready,
            => true,

            .pending,
            .replace,
            .ready,
            => false,
        };
    }

    /// Upload the pending image to the GPU and change the state of this
    /// image to ready.
    pub fn upload(
        self: *Image,
        alloc: Allocator,
        api: *const GraphicsAPI,
    ) (wuffs.Error || error{
        /// Texture creation failed, usually a GPU memory issue.
        UploadFailed,
    })!void {
        assert(self.isPending());

        // Get our pending info
        const p = self.getPendingPointer().?;

        // No error recover is required after this call because it just
        // converts in place and is idempotent.
        try p.prepForUpload(alloc);

        // Create our texture
        const texture = Texture.init(
            api.imageTextureOptions(.rgba, true),
            @intCast(p.width),
            @intCast(p.height),
            p.dataSlice(),
        ) catch return error.UploadFailed;
        errdefer comptime unreachable;

        // Uploaded. We can now clear our data and change our state.
        //
        // NOTE: For the `replace` state, this will free the old texture.
        //       We don't currently actually replace the existing texture
        //       in-place but that is an optimization we can do later.
        self.deinit(alloc);
        self.* = .{ .ready = texture };
    }

    /// Returns any pending image data for this image that requires upload.
    ///
    /// If there is no pending data to upload, returns null.
    fn getPending(self: Image) ?Pending {
        return switch (self) {
            .pending,
            .unload_pending,
            => |p| p,

            .replace,
            .unload_replace,
            => |r| r.pending,

            else => null,
        };
    }

    /// Returns the texture for this image.
    ///
    /// If there is no texture for it yet, returns null.
    fn getTexture(self: Image) ?Texture {
        return switch (self) {
            .ready,
            .unload_ready,
            => |t| t,

            .replace,
            .unload_replace,
            => |r| r.texture,

            else => null,
        };
    }

    // Same as getPending but returns a pointer instead of a copy.
    fn getPendingPointer(self: *Image) ?*Pending {
        return switch (self.*) {
            .pending => return &self.pending,
            .unload_pending => return &self.unload_pending,

            .replace => return &self.replace.pending,
            .unload_replace => return &self.unload_replace.pending,

            else => null,
        };
    }
};

test "kitty renderer ignores pending payloads and removes replaced placements" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    var t = try terminal.Terminal.init(io, alloc, .{ .rows = 3, .cols = 3 });
    defer t.deinit(alloc);
    t.width_px = 30;
    t.height_px = 30;

    var state: State = .empty;
    defer state.deinit(alloc);

    const storage = &t.screens.active.kitty_images;
    const tracked = t.screens.active.pages.countTrackedPins();
    const pending = try storage.addPendingImage(io, alloc, t.screens.active, .{
        .id = 1,
        .width = 1,
        .height = 1,
        .format = .rgba,
        .data = .{ .pending = 4 },
    });
    const pin = try t.screens.active.pages.trackPin(
        t.screens.active.cursor.page_pin.*,
    );
    try storage.addPlacement(io, alloc, t.screens.active, 1, 1, .{
        .location = .{ .pin = pin },
        .columns = 1,
        .rows = 1,
    });

    state.kittyUpdate(alloc, &t, .{ .width = 10, .height = 10 });
    try testing.expectEqual(@as(usize, 1), storage.placements.count());
    try testing.expectEqual(@as(usize, 0), state.kitty_placements.items.len);
    try testing.expect(state.images.get(.{ .kitty = 1 }) == null);

    const pixels = try alloc.dupe(u8, "rgba");
    try testing.expect(pending.complete(storage, io, pixels));
    state.kittyUpdate(alloc, &t, .{ .width = 10, .height = 10 });
    try testing.expectEqual(@as(usize, 1), state.kitty_placements.items.len);
    try testing.expectEqual(
        pending.generation,
        state.images.get(.{ .kitty = 1 }).?.generation,
    );

    // A newer pending replacement deletes the native placement and marks the
    // copied texture data for unload.
    _ = try storage.addPendingImage(io, alloc, t.screens.active, .{
        .id = 1,
        .width = 1,
        .height = 1,
        .format = .rgba,
        .data = .{ .pending = 4 },
    });
    state.kittyUpdate(alloc, &t, .{ .width = 10, .height = 10 });
    try testing.expectEqual(@as(usize, 0), storage.placements.count());
    try testing.expectEqual(tracked, t.screens.active.pages.countTrackedPins());
    try testing.expectEqual(@as(usize, 0), state.kitty_placements.items.len);
    try testing.expect(state.images.get(.{ .kitty = 1 }).?.image.isUnloading());
}

test "kitty renderer uses the intersected source rectangle" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    var t = try terminal.Terminal.init(io, alloc, .{ .rows = 3, .cols = 3 });
    defer t.deinit(alloc);
    t.width_px = 30;
    t.height_px = 30;

    var state: State = .empty;
    defer state.deinit(alloc);

    const storage = &t.screens.active.kitty_images;
    const pixels = try alloc.alloc(u8, 4 * 3 * 3);
    @memset(pixels, 0);
    try storage.addImage(io, alloc, t.screens.active, .{
        .id = 1,
        .width = 4,
        .height = 3,
        .format = .rgb,
        .data = .{ .complete = pixels },
    });
    const pin = try t.screens.active.pages.trackPin(
        t.screens.active.cursor.page_pin.*,
    );
    try storage.addPlacement(io, alloc, t.screens.active, 1, 1, .{
        .location = .{ .pin = pin },
        .source_x = 3,
        .source_y = 1,
    });

    state.kittyUpdate(alloc, &t, .{ .width = 10, .height = 10 });
    try testing.expectEqual(@as(usize, 1), state.kitty_placements.items.len);

    const placement = state.kitty_placements.items[0];
    try testing.expectEqual(@as(u32, 1), placement.width);
    try testing.expectEqual(@as(u32, 2), placement.height);
    try testing.expectEqual(@as(u32, 3), placement.source_x);
    try testing.expectEqual(@as(u32, 1), placement.source_y);
    try testing.expectEqual(@as(u32, 1), placement.source_width);
    try testing.expectEqual(@as(u32, 2), placement.source_height);
}

test "kitty renderer positions relative placements from the parent pin" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    var t = try terminal.Terminal.init(io, alloc, .{ .rows = 10, .cols = 10 });
    defer t.deinit(alloc);
    t.width_px = 100;
    t.height_px = 100;

    var state: State = .empty;
    defer state.deinit(alloc);

    const storage = &t.screens.active.kitty_images;
    const pixels = try alloc.alloc(u8, 3);
    @memset(pixels, 0);
    try storage.addImage(io, alloc, t.screens.active, .{
        .id = 1,
        .width = 1,
        .height = 1,
        .format = .rgb,
        .data = .{ .complete = pixels },
    });

    // Parent at (2, 1).
    const pin = try t.screens.active.pages.trackPin(
        t.screens.active.pages.pin(.{ .active = .{ .x = 2, .y = 1 } }).?,
    );
    try storage.addPlacement(io, alloc, t.screens.active, 1, 1, .{
        .location = .{ .pin = pin },
        .columns = 1,
        .rows = 1,
    });

    // Child offset (3, 2) cells from the parent.
    try storage.addPlacement(io, alloc, t.screens.active, 1, 2, .{
        .location = .{ .relative = .{
            .parent = .{
                .image_id = 1,
                .placement_id = .{ .tag = .external, .id = 1 },
            },
            .horizontal_offset = 3,
            .vertical_offset = 2,
        } },
        .columns = 1,
        .rows = 1,
        .z = 1,
    });

    state.kittyUpdate(alloc, &t, .{ .width = 10, .height = 10 });
    try testing.expectEqual(@as(usize, 2), state.kitty_placements.items.len);

    // Sorted by z: parent (z=0) first, child (z=1) second.
    const parent = state.kitty_placements.items[0];
    try testing.expectEqual(@as(i32, 2), parent.x);
    try testing.expectEqual(@as(i32, 1), parent.y);
    const child = state.kitty_placements.items[1];
    try testing.expectEqual(@as(i32, 5), child.x);
    try testing.expectEqual(@as(i32, 3), child.y);

    // Pin-rooted relative placements don't force per-frame rebuilds.
    try testing.expect(!state.kitty_virtual);
}

test "kitty renderer relative placement with negative offsets" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    var t = try terminal.Terminal.init(io, alloc, .{ .rows = 10, .cols = 10 });
    defer t.deinit(alloc);
    t.width_px = 100;
    t.height_px = 100;

    var state: State = .empty;
    defer state.deinit(alloc);

    const storage = &t.screens.active.kitty_images;
    const pixels = try alloc.alloc(u8, 3);
    @memset(pixels, 0);
    try storage.addImage(io, alloc, t.screens.active, .{
        .id = 1,
        .width = 1,
        .height = 1,
        .format = .rgb,
        .data = .{ .complete = pixels },
    });

    const pin = try t.screens.active.pages.trackPin(
        t.screens.active.pages.pin(.{ .active = .{ .x = 2, .y = 2 } }).?,
    );
    try storage.addPlacement(io, alloc, t.screens.active, 1, 1, .{
        .location = .{ .pin = pin },
        .columns = 1,
        .rows = 1,
    });

    // Hangs up and to the left of the viewport but still has visible
    // cells at (0, 0), so it must be kept with a negative position.
    try storage.addPlacement(io, alloc, t.screens.active, 1, 2, .{
        .location = .{ .relative = .{
            .parent = .{
                .image_id = 1,
                .placement_id = .{ .tag = .external, .id = 1 },
            },
            .horizontal_offset = -3,
            .vertical_offset = -3,
        } },
        .columns = 2,
        .rows = 2,
        .z = 1,
    });

    // Entirely off the left edge of the screen: culled.
    try storage.addPlacement(io, alloc, t.screens.active, 1, 3, .{
        .location = .{ .relative = .{
            .parent = .{
                .image_id = 1,
                .placement_id = .{ .tag = .external, .id = 1 },
            },
            .horizontal_offset = -9,
        } },
        .columns = 2,
        .rows = 2,
        .z = 2,
    });

    state.kittyUpdate(alloc, &t, .{ .width = 10, .height = 10 });
    try testing.expectEqual(@as(usize, 2), state.kitty_placements.items.len);
    const child = state.kitty_placements.items[1];
    try testing.expectEqual(@as(i32, -1), child.x);
    try testing.expectEqual(@as(i32, -1), child.y);
}

test "kitty renderer positions relative placements from virtual parent placeholders" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    var t = try terminal.Terminal.init(io, alloc, .{ .rows = 5, .cols = 5 });
    defer t.deinit(alloc);
    t.width_px = 50;
    t.height_px = 50;
    t.modes.set(.grapheme_cluster, true);

    var state: State = .empty;
    defer state.deinit(alloc);

    const storage = &t.screens.active.kitty_images;
    const pixels = try alloc.alloc(u8, 10 * 10 * 3);
    @memset(pixels, 0);
    try storage.addImage(io, alloc, t.screens.active, .{
        .id = 1,
        .width = 10,
        .height = 10,
        .format = .rgb,
        .data = .{ .complete = pixels },
    });
    try storage.addPlacement(io, alloc, t.screens.active, 1, 1, .{
        .location = .{ .virtual = {} },
    });

    // Placeholder cells for the virtual placement at (1, 1) and
    // (3, 2): the minimum cell (1, 1) is the anchor for relative
    // placements rooted at the virtual placement.
    try t.setAttribute(.{ .@"256_fg" = 1 });
    try t.setAttribute(.{ .@"256_underline_color" = 1 });
    t.screens.active.cursorAbsolute(1, 1);
    try t.printString("\u{10EEEE}\u{0305}\u{0305}");
    t.screens.active.cursorAbsolute(3, 2);
    try t.printString("\u{10EEEE}\u{0305}\u{0305}");

    // Child of the virtual placement, offset (1, 2) cells.
    try storage.addPlacement(io, alloc, t.screens.active, 1, 2, .{
        .location = .{ .relative = .{
            .parent = .{
                .image_id = 1,
                .placement_id = .{ .tag = .external, .id = 1 },
            },
            .horizontal_offset = 1,
            .vertical_offset = 2,
        } },
        .columns = 1,
        .rows = 1,
        .z = 5,
    });

    state.kittyUpdate(alloc, &t, .{ .width = 10, .height = 10 });
    try testing.expect(state.kitty_virtual);

    // Two placeholder runs (z=-1) plus the child (z=5), sorted by z.
    try testing.expectEqual(@as(usize, 3), state.kitty_placements.items.len);
    const child = state.kitty_placements.items[2];
    try testing.expectEqual(@as(i32, 5), child.z);
    try testing.expectEqual(@as(i32, 2), child.x);
    try testing.expectEqual(@as(i32, 3), child.y);
}

test "kitty renderer uploads the current animation frame" {
    const testing = std.testing;
    const alloc = testing.allocator;
    const io = testing.io;

    var t = try terminal.Terminal.init(io, alloc, .{ .rows = 3, .cols = 3 });
    defer t.deinit(alloc);
    t.width_px = 30;
    t.height_px = 30;

    var state: State = .empty;
    defer state.deinit(alloc);

    const storage = &t.screens.active.kitty_images;
    try storage.addImage(io, alloc, t.screens.active, .{
        .id = 1,
        .width = 1,
        .height = 1,
        .format = .rgba,
        .data = .{ .complete = try alloc.dupe(u8, &.{ 255, 0, 0, 255 }) },
    });
    const pin = try t.screens.active.pages.trackPin(
        t.screens.active.cursor.page_pin.*,
    );
    try storage.addPlacement(io, alloc, t.screens.active, 1, 1, .{
        .location = .{ .pin = pin },
        .columns = 1,
        .rows = 1,
    });

    state.kittyUpdate(alloc, &t, .{ .width = 10, .height = 10 });
    const gen1 = state.images.get(.{ .kitty = 1 }).?.generation;
    try testing.expectEqualSlices(
        u8,
        &.{ 255, 0, 0, 255 },
        state.images.get(.{ .kitty = 1 }).?.image.pending.dataSlice(),
    );

    // Attach an animation and make its extra frame current, the way
    // an animation tick would.
    const img = storage.images.getPtr(1).?;
    const anim = try alloc.create(terminal.kitty.graphics.Animation);
    anim.* = .{};
    img.animation = anim;
    try anim.frames.append(alloc, .{
        .data = try alloc.dupe(u8, &.{ 0, 0, 255, 255 }),
        .gap_ms = 40,
    });
    anim.current_index = 1;
    storage.markImageContentChanged(io, img);

    // The renderer must pick up the frame's pixels under a fresh
    // generation.
    state.kittyUpdate(alloc, &t, .{ .width = 10, .height = 10 });
    const entry = state.images.get(.{ .kitty = 1 }).?;
    try testing.expect(entry.generation > gen1);
    try testing.expectEqualSlices(
        u8,
        &.{ 0, 0, 255, 255 },
        entry.image.pending.dataSlice(),
    );
}

test "Image.markForUnload pending -> unload_pending" {
    const testing = std.testing;
    const alloc = testing.allocator;

    // A four-byte RGBA pixel buffer the .pending state takes
    // ownership of; deinit frees it.
    const data = try alloc.alloc(u8, 4);
    var img: Image = .{ .pending = .{
        .width = 1,
        .height = 1,
        .pixel_format = .rgba,
        .data = data.ptr,
    } };
    defer img.deinit(alloc);

    try testing.expect(img.isPending());
    try testing.expect(!img.isUnloading());

    img.markForUnload();

    try testing.expectEqual(
        @as(std.meta.Tag(Image), .unload_pending),
        std.meta.activeTag(img),
    );
    // isUnloading() being true is what State.upload checks first;
    // the unload sweep deinits + removes the image before the
    // retry-via-isPending branch fires, so the failed image only
    // logs once instead of every frame.
    try testing.expect(img.isUnloading());
}

test "Image.markForUnload is idempotent on already-unloading states" {
    const testing = std.testing;
    const alloc = testing.allocator;

    const data = try alloc.alloc(u8, 4);
    var img: Image = .{ .unload_pending = .{
        .width = 1,
        .height = 1,
        .pixel_format = .rgba,
        .data = data.ptr,
    } };
    defer img.deinit(alloc);

    img.markForUnload();
    try testing.expectEqual(
        @as(std.meta.Tag(Image), .unload_pending),
        std.meta.activeTag(img),
    );
}

test "imageStagingBytes aligns row pitch to 256" {
    // 1x1 RGBA -> aligned row pitch is 256, height 1 -> 256 bytes.
    try std.testing.expectEqual(@as(u64, 256), imageStagingBytes(1, 1));
}

test "imageStagingBytes for 64x64 RGBA" {
    // 64 * 4 = 256, already aligned. 256 * 64 = 16384.
    try std.testing.expectEqual(@as(u64, 16384), imageStagingBytes(64, 64));
}

test "imageStagingBytes pads to next 256 for non-aligned widths" {
    // 65 * 4 = 260, pads to 512. 512 * 65 = 33280.
    try std.testing.expectEqual(@as(u64, 33280), imageStagingBytes(65, 65));
}

test "imageStagingBytes for typical 4096x3072 RGBA upload" {
    // 4096 * 4 = 16384, already aligned. 16384 * 3072 = 50331648 (~48 MiB).
    try std.testing.expectEqual(@as(u64, 50331648), imageStagingBytes(4096, 3072));
}

test "wouldExceedBudget false when sum fits exactly" {
    // 100 + 28 == 128, fits at the boundary.
    try std.testing.expect(!wouldExceedBudget(100, 28, 128));
}

test "wouldExceedBudget true when sum overshoots by one" {
    try std.testing.expect(wouldExceedBudget(100, 29, 128));
}

test "wouldExceedBudget false when in_flight already at budget and est is 0" {
    try std.testing.expect(!wouldExceedBudget(128, 0, 128));
}

test "wouldExceedBudget true when in_flight already over budget" {
    // Already over (somehow) -- any non-zero est should exceed.
    try std.testing.expect(wouldExceedBudget(200, 1, 128));
}

test "Image.estimatedUploadStagingBytes returns imageStagingBytes for .pending" {
    var data: [16]u8 = .{0} ** 16;
    const img: Image = .{ .pending = .{
        .width = 2,
        .height = 2,
        .pixel_format = .rgba,
        .data = &data,
    } };
    // 2x2 RGBA -> aligned pitch 256 * height 2 = 512.
    try std.testing.expectEqual(@as(u64, 512), img.estimatedUploadStagingBytes());
}

test "Image.estimatedUploadStagingBytes returns 0 for .ready" {
    // .ready carries a Texture, and OpenGL's Texture has no default-init
    // fields, so `.ready = .{}` only compiles where Texture is all-defaults.
    // Gating on the DX12-only marker keeps the test active where it matters.
    if (comptime @hasField(Texture, "pending_staging_bytes")) {
        const img: Image = .{ .ready = .{} };
        try std.testing.expectEqual(@as(u64, 0), img.estimatedUploadStagingBytes());
    } else {
        return error.SkipZigTest;
    }
}

test "Image.estimatedUploadStagingBytes returns imageStagingBytes for .replace" {
    if (comptime @hasField(Texture, "pending_staging_bytes")) {
        var data: [16]u8 = .{0} ** 16;
        const img: Image = .{ .replace = .{
            .texture = .{},
            .pending = .{
                .width = 2,
                .height = 2,
                .pixel_format = .rgba,
                .data = &data,
            },
        } };
        try std.testing.expectEqual(@as(u64, 512), img.estimatedUploadStagingBytes());
    } else {
        return error.SkipZigTest;
    }
}

test "Image.pendingStagingBytes returns texture.pending_staging_bytes for .ready" {
    // Only the DX12 Texture has the staging-bytes counter. The outer
    // comptime branch keeps the field access out of Metal/OpenGL
    // analysis; the inner runtime skip surfaces in the test report.
    if (comptime @hasField(Texture, "pending_staging_bytes")) {
        const img: Image = .{ .ready = .{ .pending_staging_bytes = 12345 } };
        try std.testing.expectEqual(@as(u64, 12345), img.pendingStagingBytes());
    } else {
        return error.SkipZigTest;
    }
}

test "Image.pendingStagingBytes returns 0 for .pending (no texture yet)" {
    var data: [4]u8 = .{0} ** 4;
    const img: Image = .{ .pending = .{
        .width = 1,
        .height = 1,
        .pixel_format = .rgba,
        .data = &data,
    } };
    try std.testing.expectEqual(@as(u64, 0), img.pendingStagingBytes());
}

test "State.upload_budget_bytes defaults to TEXTURE_UPLOAD_BUDGET_DEFAULT_BYTES" {
    const state = State.empty;
    try std.testing.expectEqual(
        TEXTURE_UPLOAD_BUDGET_DEFAULT_BYTES,
        state.upload_budget_bytes,
    );
}
