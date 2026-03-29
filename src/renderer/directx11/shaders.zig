//! Shader management and GPU data structs for DX11.
//!
//! The data structs (Uniforms, CellText, CellBg, Image, BgImage) must match
//! Metal's layout since GenericRenderer writes to them directly. The actual
//! HLSL shaders will interpret these differently but the CPU-side layout is
//! shared across backends.
//!
//! TODO: add comptime assertions cross-referencing @sizeOf against Metal's
//! data structs to catch layout drift across backends.
const std = @import("std");
const math = @import("../../math.zig");
const builtin = @import("builtin");
const com = @import("com.zig");
const d3d11 = @import("d3d11.zig");
const dxgi = @import("dxgi.zig");

const Pipeline = @import("Pipeline.zig");

/// Embedded shader bytecode -- compiled at build time by HlslStep.zig.
/// On non-Windows these are empty slices so the module still compiles.
const shader_bytecode = if (builtin.os.tag == .windows) struct {
    const bg_color_vs = @embedFile("ghostty_hlsl_bg_color_vs");
    const bg_color_ps = @embedFile("ghostty_hlsl_bg_color_ps");
    const cell_bg_ps = @embedFile("ghostty_hlsl_cell_bg_ps");
    const cell_text_vs = @embedFile("ghostty_hlsl_cell_text_vs");
    const cell_text_ps = @embedFile("ghostty_hlsl_cell_text_ps");
    const image_vs = @embedFile("ghostty_hlsl_image_vs");
    const image_ps = @embedFile("ghostty_hlsl_image_ps");
    const bg_image_vs = @embedFile("ghostty_hlsl_bg_image_vs");
    const bg_image_ps = @embedFile("ghostty_hlsl_bg_image_ps");
} else struct {
    const bg_color_vs: []const u8 = &.{};
    const bg_color_ps: []const u8 = &.{};
    const cell_bg_ps: []const u8 = &.{};
    const cell_text_vs: []const u8 = &.{};
    const cell_text_ps: []const u8 = &.{};
    const image_vs: []const u8 = &.{};
    const image_ps: []const u8 = &.{};
    const bg_image_vs: []const u8 = &.{};
    const bg_image_ps: []const u8 = &.{};
};

// --- Input element descriptions for instanced pipelines ---
const PER_INSTANCE = d3d11.D3D11_INPUT_CLASSIFICATION.PER_INSTANCE_DATA;

/// Input layout for CellText instances.
const cell_text_input_elements = [_]d3d11.D3D11_INPUT_ELEMENT_DESC{
    .{ // glyph_pos: [2]u32
        .SemanticName = "GLYPH_POS",
        .SemanticIndex = 0,
        .Format = .R32G32_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 0,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // glyph_size: [2]u32
        .SemanticName = "GLYPH_SIZE",
        .SemanticIndex = 0,
        .Format = .R32G32_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 8,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // bearings: [2]i16
        .SemanticName = "BEARINGS",
        .SemanticIndex = 0,
        .Format = .R16G16_SINT,
        .InputSlot = 0,
        .AlignedByteOffset = 16,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // grid_pos: [2]u16
        .SemanticName = "GRID_POS",
        .SemanticIndex = 0,
        .Format = .R16G16_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 20,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // color: [4]u8
        .SemanticName = "COLOR",
        .SemanticIndex = 0,
        .Format = .R8G8B8A8_UNORM,
        .InputSlot = 0,
        .AlignedByteOffset = 24,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // atlas: Atlas (u8)
        .SemanticName = "ATLAS",
        .SemanticIndex = 0,
        .Format = .R8_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 28,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // bools: packed struct(u8)
        .SemanticName = "BOOLS",
        .SemanticIndex = 0,
        .Format = .R8_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 29,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
};

/// Input layout for Image instances.
const image_input_elements = [_]d3d11.D3D11_INPUT_ELEMENT_DESC{
    .{ // grid_pos: [2]f32
        .SemanticName = "GRID_POS",
        .SemanticIndex = 0,
        .Format = .R32G32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 0,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // cell_offset: [2]f32
        .SemanticName = "CELL_OFFSET",
        .SemanticIndex = 0,
        .Format = .R32G32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 8,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // source_rect: [4]f32
        .SemanticName = "SOURCE_RECT",
        .SemanticIndex = 0,
        .Format = .R32G32B32A32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 16,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // dest_size: [2]f32
        .SemanticName = "DEST_SIZE",
        .SemanticIndex = 0,
        .Format = .R32G32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 32,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
};

/// Input layout for BgImage instances.
const bg_image_input_elements = [_]d3d11.D3D11_INPUT_ELEMENT_DESC{
    .{ // opacity: f32
        .SemanticName = "OPACITY",
        .SemanticIndex = 0,
        .Format = .R32_FLOAT,
        .InputSlot = 0,
        .AlignedByteOffset = 0,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
    .{ // info: packed struct(u8)
        .SemanticName = "INFO",
        .SemanticIndex = 0,
        .Format = .R8_UINT,
        .InputSlot = 0,
        .AlignedByteOffset = 4,
        .InputSlotClass = PER_INSTANCE,
        .InstanceDataStepRate = 1,
    },
};

comptime {
    std.debug.assert(@sizeOf(CellText) == 32);
    std.debug.assert(@offsetOf(CellText, "bools") == 29);
    std.debug.assert(@sizeOf(Image) == 40);
    std.debug.assert(@offsetOf(Image, "dest_size") == 32);
    std.debug.assert(@sizeOf(BgImage) == 8);
    std.debug.assert(@offsetOf(BgImage, "info") == 4);
}

/// Shader management for DX11.
pub const Shaders = struct {
    pipelines: Pipelines = .{},
    post_pipelines: []const Pipeline = &.{},
    defunct: bool = false,

    pub const Pipelines = struct {
        bg_color: Pipeline = .{},
        cell_bg: Pipeline = .{},
        cell_text: Pipeline = .{},
        image: Pipeline = .{},
        bg_image: Pipeline = .{},
    };

    pub const InitError = Pipeline.InitError;

    /// Initialize all 5 pipelines from embedded shader bytecode.
    /// On non-Windows, returns default-initialized (empty) pipelines.
    pub fn init(device: ?*d3d11.ID3D11Device) InitError!Shaders {
        const dev = device orelse return .{};

        return .{
            .pipelines = .{
                .bg_color = try Pipeline.init(.{
                    .device = dev,
                    .vs_bytecode = shader_bytecode.bg_color_vs,
                    .ps_bytecode = shader_bytecode.bg_color_ps,
                }),
                .cell_bg = try Pipeline.init(.{
                    .device = dev,
                    .vs_bytecode = shader_bytecode.bg_color_vs, // shared VS
                    .ps_bytecode = shader_bytecode.cell_bg_ps,
                }),
                .cell_text = try Pipeline.init(.{
                    .device = dev,
                    .vs_bytecode = shader_bytecode.cell_text_vs,
                    .ps_bytecode = shader_bytecode.cell_text_ps,
                    .input_elements = &cell_text_input_elements,
                    .instance_stride = @sizeOf(CellText),
                }),
                .image = try Pipeline.init(.{
                    .device = dev,
                    .vs_bytecode = shader_bytecode.image_vs,
                    .ps_bytecode = shader_bytecode.image_ps,
                    .input_elements = &image_input_elements,
                    .instance_stride = @sizeOf(Image),
                }),
                .bg_image = try Pipeline.init(.{
                    .device = dev,
                    .vs_bytecode = shader_bytecode.bg_image_vs,
                    .ps_bytecode = shader_bytecode.bg_image_ps,
                    .input_elements = &bg_image_input_elements,
                    .instance_stride = @sizeOf(BgImage),
                }),
            },
        };
    }

    pub fn deinit(self: *Shaders, alloc: std.mem.Allocator) void {
        for (self.post_pipelines) |p| {
            p.deinit();
        }
        if (self.post_pipelines.len > 0) {
            alloc.free(self.post_pipelines);
        }
        self.post_pipelines = &.{};

        self.pipelines.bg_color.deinit();
        self.pipelines.cell_bg.deinit();
        self.pipelines.cell_text.deinit();
        self.pipelines.image.deinit();
        self.pipelines.bg_image.deinit();
        self.pipelines = .{};
    }
};

/// GPU uniform values for the cell shaders.
pub const Uniforms = extern struct {
    projection_matrix: math.Mat align(16),
    screen_size: [2]f32 align(8),
    cell_size: [2]f32 align(8),
    grid_size: [2]u16 align(4),
    /// The padding around the terminal grid in pixels. In order:
    /// top, right, bottom, left.
    grid_padding: [4]f32 align(16),
    padding_extend: PaddingExtend align(1),
    min_contrast: f32 align(4),
    cursor_pos: [2]u16 align(4),
    cursor_color: [4]u8 align(4),
    bg_color: [4]u8 align(4),

    bools: extern struct {
        cursor_wide: bool align(1),
        use_display_p3: bool align(1),
        use_linear_blending: bool align(1),
        use_linear_correction: bool align(1) = false,
    },

    const PaddingExtend = packed struct(u8) {
        left: bool = false,
        right: bool = false,
        up: bool = false,
        down: bool = false,
        _padding: u4 = 0,
    };
};

/// Single parameter for the cell text shader.
pub const CellText = extern struct {
    glyph_pos: [2]u32 align(8) = .{ 0, 0 },
    glyph_size: [2]u32 align(8) = .{ 0, 0 },
    bearings: [2]i16 align(4) = .{ 0, 0 },
    grid_pos: [2]u16 align(4),
    color: [4]u8 align(4),
    atlas: Atlas align(1),
    bools: packed struct(u8) {
        no_min_contrast: bool = false,
        is_cursor_glyph: bool = false,
        _padding: u6 = 0,
    } align(1) = .{},

    pub const Atlas = enum(u8) {
        grayscale = 0,
        color = 1,
    };

    test {
        try std.testing.expectEqual(32, @sizeOf(CellText));
    }
};

/// Single parameter for the cell bg shader.
pub const CellBg = [4]u8;

/// Single parameter for the image shader.
pub const Image = extern struct {
    grid_pos: [2]f32,
    cell_offset: [2]f32,
    source_rect: [4]f32,
    dest_size: [2]f32,
};

/// Single parameter for the bg image shader.
pub const BgImage = extern struct {
    opacity: f32 align(4),
    info: Info align(1),

    pub const Info = packed struct(u8) {
        position: Position,
        fit: Fit,
        repeat: bool,
        _padding: u1 = 0,

        pub const Position = enum(u4) {
            tl = 0,
            tc = 1,
            tr = 2,
            ml = 3,
            mc = 4,
            mr = 5,
            bl = 6,
            bc = 7,
            br = 8,
        };

        pub const Fit = enum(u2) {
            contain = 0,
            cover = 1,
            stretch = 2,
            none = 3,
        };
    };
};
