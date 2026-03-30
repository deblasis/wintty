// HLSL shaders for the Ghostty DX11 renderer.
//
// Implements all 5 pipelines: bg_color, cell_bg, cell_text, image, bg_image.
//
// Each pipeline is compiled separately (one VS + one PS per pipeline). All
// entry points live in this single file so the cbuffer declaration and
// helpers are shared. The build system invokes fxc/dxc once per entry point.
//
// Texture and structured-buffer declarations appear once per logical binding
// slot. Because pipelines are compiled separately the compiler only sees the
// resources that each entry point actually references.
//
// ---- cbuffer layout -------------------------------------------------------
//
// The cbuffer must match the byte layout of the Zig Uniforms extern struct in
// src/renderer/directx11/shaders.zig. GenericRenderer writes the struct
// directly to the GPU buffer, byte-for-byte.
//
// HLSL's default cbuffer packing rules do NOT match C struct layout: HLSL
// will not split a field across a 16-byte register boundary but C will. We
// must use explicit packoffset for every field.
//
// Zig struct byte offsets (extern struct = C layout):
//   0:   projection_matrix  [4][4]f32  64 bytes  align 16
//   64:  screen_size        [2]f32      8 bytes  align  8
//   72:  cell_size          [2]f32      8 bytes  align  8
//   80:  grid_size          [2]u16      4 bytes  align  4
//   84:  <12 bytes of C padding to reach align 16>
//   96:  grid_padding       [4]f32     16 bytes  align 16
//   112: padding_extend     u8          1 byte   align  1
//   113: <3 bytes of C padding>
//   116: min_contrast       f32         4 bytes  align  4
//   120: cursor_pos         [2]u16      4 bytes  align  4
//   124: cursor_color       [4]u8       4 bytes  align  4
//   128: bg_color           [4]u8       4 bytes  align  4
//   132: bools              [4]bool     4 bytes  align  1 each
//   Total: 136 bytes, padded to 144 (cbuffer size must be a multiple of 16)
//
// HLSL cbuffer register mapping (each c-register is 16 bytes / float4):
//   c0..c3   bytes  0..63   projection_matrix float4x4
//   c4.xy    bytes 64..71   screen_size       float2
//   c4.zw    bytes 72..79   cell_size         float2
//   c5.x     bytes 80..83   grid_size_packed  uint   (2x u16, low=x high=y)
//   c5.yzw   bytes 84..95   <gap, C padding>
//   c6       bytes 96..111  grid_padding      float4
//   c7.x     bytes 112..115 padding_extend_packed uint (low byte used)
//   c7.y     bytes 116..119 min_contrast      float
//   c7.z     bytes 120..123 cursor_pos_packed uint   (2x u16)
//   c7.w     bytes 124..127 cursor_color_packed uint (4x u8)
//   c8.x     bytes 128..131 bg_color_packed   uint   (4x u8)
//   c8.y     bytes 132..135 bools_packed      uint   (4x bool, one per byte)
// ---------------------------------------------------------------------------

cbuffer Uniforms : register(b0)
{
    float4x4 projection_matrix    : packoffset(c0);
    float2   screen_size          : packoffset(c4.x);
    float2   cell_size            : packoffset(c4.z);
    uint     grid_size_packed     : packoffset(c5.x);
    float4   grid_padding         : packoffset(c6.x);
    uint     padding_extend_packed: packoffset(c7.x);
    float    min_contrast         : packoffset(c7.y);
    uint     cursor_pos_packed    : packoffset(c7.z);
    uint     cursor_color_packed  : packoffset(c7.w);
    uint     bg_color_packed      : packoffset(c8.x);
    uint     bools_packed         : packoffset(c8.y);
}

// ---------------------------------------------------------------------------
// Padding extend flags (match Zig PaddingExtend packed struct bit positions)
// ---------------------------------------------------------------------------
#define EXTEND_LEFT  1u
#define EXTEND_RIGHT 2u
#define EXTEND_UP    4u
#define EXTEND_DOWN  8u

// ---------------------------------------------------------------------------
// CellText atlas and bools flags
// ---------------------------------------------------------------------------
#define ATLAS_GRAYSCALE  0u
#define ATLAS_COLOR      1u

#define NO_MIN_CONTRAST  1u
#define IS_CURSOR_GLYPH  2u

// TODO: port from Metal -- fit/position/repeat logic (BgImage.Info bit masks)

// ---------------------------------------------------------------------------
// Uniform helpers
// ---------------------------------------------------------------------------

uint2 unpack_grid_size()
{
    // grid_size is stored as two u16 packed into one uint: low word = x, high word = y.
    return uint2(grid_size_packed & 0xFFFFu, (grid_size_packed >> 16u) & 0xFFFFu);
}

// TODO: re-enable padding_extend once DX11 blend state reliably composites
// transparent pixels.  Until then, CellBgPS always clamps to the nearest
// edge cell instead of returning transparent for non-extended padding.
bool padding_extend_left()  { return (padding_extend_packed & EXTEND_LEFT)  != 0u; }
bool padding_extend_right() { return (padding_extend_packed & EXTEND_RIGHT) != 0u; }
bool padding_extend_up()    { return (padding_extend_packed & EXTEND_UP)    != 0u; }
bool padding_extend_down()  { return (padding_extend_packed & EXTEND_DOWN)  != 0u; }

// ---------------------------------------------------------------------------
// Color helpers
//
// Simplified vs. Metal: no Display P3 conversion, no linearize/unlinearize,
// no minimum-contrast enforcement.
// TODO: port full color pipeline from src/renderer/shaders/shaders.metal
// ---------------------------------------------------------------------------

// Unpack a uint containing RGBA as four u8 bytes (R in the low byte) to
// a float4 in [0, 1]. Matches how Zig stores [4]u8 in little-endian memory.
float4 unpack_u32_rgba(uint packed)
{
    return float4(
        float((packed      ) & 0xFFu),
        float((packed >>  8) & 0xFFu),
        float((packed >> 16) & 0xFFu),
        float((packed >> 24) & 0xFFu)
    ) / 255.0;
}

// load_color: simplified -- converts a packed uint8x4 (RGBA) to float4.
// Does NOT linearize, convert to Display P3, or apply premultiplication.
// TODO: port full load_color() from shaders.metal
float4 load_color(uint packed)
{
    return unpack_u32_rgba(packed);
}

// Variant for colors that arrived via hardware R8G8B8A8_UNORM conversion
// (e.g. the CellText color instance attribute). Already in [0, 1].
// TODO: linearize, Display P3, min-contrast -- port from Metal
float4 load_color_f4(float4 color)
{
    return color;
}

// ---------------------------------------------------------------------------
// Shared: full-screen triangle vertex generation
//
// Generates an oversized single triangle from SV_VertexID that covers the
// entire viewport once clipped. Avoids the need for a vertex buffer in
// full-screen passes (bg_color, cell_bg, bg_image).
//
//   vid == 0: (-1, -3)   bottom-left, off screen
//   vid == 1: (-1,  1)   top-left
//   vid == 2: ( 3,  1)   top-right, off screen
//
//  X  <- vid 0: (-1, -3)
//  |\
//  | \
//  |##\
//  |#+#\   + is NDC origin, # is the viewport area
//  X----X  <- vid 2: (3, 1)
//  ^
//  vid 1: (-1, 1)
// ---------------------------------------------------------------------------

struct FullScreenVSOut
{
    float4 position : SV_POSITION;
};

FullScreenVSOut FullScreenTriangle(uint vid)
{
    FullScreenVSOut o;
    o.position.x  = (vid == 2u) ? 3.0 : -1.0;
    o.position.y  = (vid == 0u) ? -3.0 : 1.0;
    o.position.zw = 1.0;
    return o;
}

// ===========================================================================
// Pipeline 1: bg_color
//
//   VS: BgColorVS  -- full-screen triangle, no per-vertex or per-instance data
//   PS: BgColorPS  -- returns the uniform background color
// ===========================================================================

FullScreenVSOut BgColorVS(uint vid : SV_VertexID)
{
    return FullScreenTriangle(vid);
}

float4 BgColorPS(FullScreenVSOut input) : SV_TARGET
{
    return load_color(bg_color_packed);
}

// ===========================================================================
// Pipeline 2: cell_bg
//
//   VS: BgColorVS  (reused entry point declared above)
//   PS: CellBgPS   -- looks up per-cell background color from a flat buffer
//
// The CPU side binds an array of packed RGBA u32 values indexed by grid
// position (row-major: index = y * grid_width + x).
// In DX11 this is a StructuredBuffer<uint> at t0.
// ===========================================================================

StructuredBuffer<uint> cell_bg_colors : register(t0);

float4 CellBgPS(FullScreenVSOut input) : SV_TARGET
{
    // grid_padding stores [top, right, bottom, left].
    // Metal uses .wx to get (left, top) as the padding offset.
    // .w = index 3 = left, .x = index 0 = top.
    float2 padding_offset = float2(grid_padding.w, grid_padding.x);

    int2 grid_pos = int2(floor((input.position.xy - padding_offset) / cell_size));

    uint2 gs = unpack_grid_size();

    // Clamp out-of-bounds grid positions to the nearest edge cell.
    //
    // Metal and OpenGL return transparent here (unless padding_extend is
    // set) and rely on alpha blending to let the bg_color pass show
    // through.  On DX11 the blend state does not reliably composite
    // transparent pixels, so we always clamp to the edge cell.  This
    // also avoids a visible color mismatch when the terminal sets per-
    // cell backgrounds (e.g. cmd.exe) that differ from bg_color.
    if (grid_pos.x < 0)
        grid_pos.x = 0;
    else if (grid_pos.x >= (int)gs.x)
        grid_pos.x = (int)gs.x - 1;

    if (grid_pos.y < 0)
        grid_pos.y = 0;
    else if (grid_pos.y >= (int)gs.y)
        grid_pos.y = (int)gs.y - 1;

    uint idx = (uint)grid_pos.y * gs.x + (uint)grid_pos.x;
    float4 bg = load_color(cell_bg_colors[idx]);

    // Composite over the global background so the result is fully opaque.
    // On Metal/OpenGL this compositing happens via hardware blending, but
    // DX11's blend state is unreliable for this.  Doing it in the shader
    // produces the same result: out = cell_bg + bg_color * (1 - cell_bg.a).
    float4 global_bg = load_color(bg_color_packed);
    bg = float4(bg.rgb + global_bg.rgb * (1.0 - bg.a), 1.0);
    return bg;
}

// ===========================================================================
// Pipeline 3: cell_text
//
//   VS: CellTextVS  -- instanced triangle strip, 4 vertices per glyph quad
//   PS: CellTextPS  -- samples the atlas texture (grayscale or color)
//
// Instance buffer layout (32 bytes, matches Zig CellText extern struct):
//   glyph_pos   uint2   GLYPH_POS    position in atlas (R32G32_UINT)
//   glyph_size  uint2   GLYPH_SIZE   size in atlas     (R32G32_UINT)
//   bearings    int2    BEARINGS     bearing offsets   (R16G16_SINT)
//   grid_pos    uint2   GRID_POS     cell coordinate   (R16G16_UINT)
//   color       float4  COLOR        fg color          (R8G8B8A8_UNORM)
//   atlas       uint    ATLAS        atlas index       (R8_UINT)
//   bools       uint    BOOLS        packed flags      (R8_UINT)
//
// Texture bindings:
//   t0 = grayscale atlas (Texture2D<float4>)
//   t1 = color atlas     (Texture2D<float4>)
//   t2 = cell bg colors  (StructuredBuffer<uint>)
// ===========================================================================

Texture2D<float4>  ct_atlas_grayscale   : register(t0);
Texture2D<float4>  ct_atlas_color       : register(t1);
StructuredBuffer<uint> ct_cell_bg_colors: register(t2);

struct CellTextVSIn
{
    uint2  glyph_pos  : GLYPH_POS;
    uint2  glyph_size : GLYPH_SIZE;
    int2   bearings   : BEARINGS;
    uint2  grid_pos   : GRID_POS;
    float4 color      : COLOR;
    uint   atlas      : ATLAS;
    uint   bools      : BOOLS;
};

struct CellTextVSOut
{
    float4 position  : SV_POSITION;
    nointerpolation float4 color    : COLOR0;
    nointerpolation float4 bg_color : COLOR1;
    float2 tex_coord : TEXCOORD0;
    nointerpolation uint   atlas    : ATLAS;
};

CellTextVSOut CellTextVS(uint vid : SV_VertexID, CellTextVSIn inst)
{
    // Triangle strip corner selection:
    //
    //   0 --> 1
    //   |   .'|
    //   |  /  |
    //   | L   |
    //   2 --> 3
    //
    // 0 = top-left  (0, 0)
    // 1 = top-right (1, 0)
    // 2 = bot-left  (0, 1)
    // 3 = bot-right (1, 1)
    float2 corner;
    corner.x = float(vid == 1u || vid == 3u);
    corner.y = float(vid == 2u || vid == 3u);

    // Convert grid cell coordinate to world-space pixels.
    float2 cell_pos = cell_size * float2(inst.grid_pos);

    float2 size   = float2(inst.glyph_size);
    float2 offset = float2(inst.bearings);

    // Y bearing is the distance from the cell bottom to the glyph top.
    // Subtract from cell height to get the pixel offset from the cell top.
    offset.y = cell_size.y - offset.y;

    cell_pos = cell_pos + size * corner + offset;

    CellTextVSOut o;
    o.position  = mul(projection_matrix, float4(cell_pos.x, cell_pos.y, 0.0, 1.0));

    // Texture coordinate in pixel space (not normalized).
    // Load() requires integer coords so the PS truncates to int2.
    o.tex_coord = float2(inst.glyph_pos) + float2(inst.glyph_size) * corner;

    o.atlas = inst.atlas;

    // Foreground color (R8G8B8A8_UNORM hardware-converted to [0,1] float4).
    // TODO: linearize, Display P3, min-contrast -- port from Metal
    o.color = load_color_f4(inst.color);

    // Per-cell background color blended with the global background.
    uint2 gs = unpack_grid_size();
    uint bg_idx = inst.grid_pos.y * gs.x + inst.grid_pos.x;
    float4 cell_bg   = load_color(ct_cell_bg_colors[bg_idx]);
    float4 global_bg = load_color(bg_color_packed);
    o.bg_color = cell_bg + global_bg * (1.0 - cell_bg.a);

    // Cursor color override: if this cell sits at the cursor position but is
    // not itself the cursor glyph, replace the fg color with cursor_color.
    uint2 cursor_pos  = uint2(cursor_pos_packed & 0xFFFFu,
                              (cursor_pos_packed >> 16u) & 0xFFFFu);
    bool cursor_wide  = (bools_packed & 1u) != 0u;
    bool is_cursor_pos = (
        inst.grid_pos.x == cursor_pos.x ||
        (cursor_wide && inst.grid_pos.x == cursor_pos.x + 1u)
    ) && inst.grid_pos.y == cursor_pos.y;

    bool is_cursor_glyph = (inst.bools & IS_CURSOR_GLYPH) != 0u;
    if (!is_cursor_glyph && is_cursor_pos) {
        o.color = load_color(cursor_color_packed);
    }

    return o;
}

float4 CellTextPS(CellTextVSOut input) : SV_TARGET
{
    // Load() uses integer texel coordinates (pixel-coordinate mode, no
    // filtering), matching Metal's coord::pixel + filter::nearest sampler.
    int2 tc = int2(input.tex_coord);

    if (input.atlas == ATLAS_COLOR) {
        // Color glyph: sample from the color atlas.
        // Values arrive already premultiplied; do not premultiply again.
        // TODO: unlinearize if !use_linear_blending -- port from Metal
        float4 color = ct_atlas_color.Load(int3(tc, 0));
        return color;
    }

    // Grayscale glyph: the red channel is an alpha mask applied to fg color.
    // TODO: linear blending correction, unlinearize -- port from Metal
    float4 color = input.color;
    float a = ct_atlas_grayscale.Load(int3(tc, 0)).r;
    color *= a;
    return color;
}

// ===========================================================================
// Pipeline 4: image
//
//   VS: ImageVS  -- instanced triangle strip, 4 vertices per image quad
//   PS: ImagePS  -- samples the image texture at pixel coordinates
//
// Instance buffer layout (40 bytes, matches Zig Image extern struct):
//   grid_pos    float2  GRID_POS     cell coordinate
//   cell_offset float2  CELL_OFFSET  pixel offset within cell
//   source_rect float4  SOURCE_RECT  (x, y, w, h) in texels
//   dest_size   float2  DEST_SIZE    destination size in pixels
//
// Texture binding: t0 = image texture (Texture2D<float4>)
// ===========================================================================

Texture2D<float4> img_texture : register(t0);

struct ImageVSIn
{
    float2 grid_pos    : GRID_POS;
    float2 cell_offset : CELL_OFFSET;
    float4 source_rect : SOURCE_RECT;
    float2 dest_size   : DEST_SIZE;
};

struct ImageVSOut
{
    float4 position  : SV_POSITION;
    float2 tex_coord : TEXCOORD0;
};

ImageVSOut ImageVS(uint vid : SV_VertexID, ImageVSIn inst)
{
    // Triangle strip corners -- same layout as cell_text.
    float2 corner;
    corner.x = float(vid == 1u || vid == 3u);
    corner.y = float(vid == 2u || vid == 3u);

    // Texture coordinate: source origin + corner * source size.
    // Not normalized; Load() uses pixel coords so no division by tex size needed.
    float2 tex_coord = inst.source_rect.xy + inst.source_rect.zw * corner;

    // World position: top-left of grid cell + cell_offset + dest_size * corner.
    float2 image_pos = (cell_size * inst.grid_pos) + inst.cell_offset;
    image_pos += inst.dest_size * corner;

    ImageVSOut o;
    o.position  = mul(projection_matrix, float4(image_pos.x, image_pos.y, 0.0, 1.0));
    o.tex_coord = tex_coord;
    return o;
}

float4 ImagePS(ImageVSOut input) : SV_TARGET
{
    int2 tc = int2(input.tex_coord);
    float4 rgba = img_texture.Load(int3(tc, 0));

    // TODO: unlinearize if !use_linear_blending -- port from Metal
    rgba.rgb *= rgba.a;
    return rgba;
}

// ===========================================================================
// Pipeline 5: bg_image
//
//   VS: BgImageVS  -- full-screen triangle with per-instance data
//   PS: BgImagePS  -- samples image, blends it over the bg color
//
// Instance buffer layout (8 bytes, matches Zig BgImage extern struct):
//   opacity  float  OPACITY
//   info     uint   INFO    packed byte: position[3:0], fit[5:4], repeat[6]
//
// Texture binding:
//   t0 = background image (Texture2D<float4>)
//   s0 = linear sampler
// ===========================================================================

Texture2D<float4> bgi_texture : register(t0);
SamplerState      bgi_sampler : register(s0);

struct BgImageVSIn
{
    float opacity : OPACITY;
    uint  info    : INFO;
};

struct BgImageVSOut
{
    float4 position  : SV_POSITION;
    nointerpolation float4 bg_color : COLOR0;
    nointerpolation float  opacity  : TEXCOORD0;
};

BgImageVSOut BgImageVS(uint vid : SV_VertexID, BgImageVSIn inst)
{
    BgImageVSOut o;

    // Full-screen triangle (same geometry as BgColorVS).
    o.position.x  = (vid == 2u) ? 3.0 : -1.0;
    o.position.y  = (vid == 0u) ? -3.0 : 1.0;
    o.position.zw = 1.0;

    o.opacity  = inst.opacity;
    o.bg_color = load_color(bg_color_packed);

    // TODO: port from Metal -- fit/position/repeat logic

    return o;
}

float4 BgImagePS(BgImageVSOut input) : SV_TARGET
{
    // TODO: port from Metal -- fit/position/repeat logic

    // Sample the texture at the normalized screen UV.
    float2 uv = input.position.xy / screen_size;
    float4 rgba = bgi_texture.SampleLevel(bgi_sampler, uv, 0.0);

    // Premultiply alpha.
    // TODO: unlinearize if !use_linear_blending -- port from Metal
    rgba.rgb *= rgba.a;

    float bg_alpha = input.bg_color.a;

    // Cap opacity at the value that makes the image fully opaque relative to
    // the background color alpha, to avoid over-exposure (matching Metal).
    float effective_opacity = min(
        input.opacity,
        bg_alpha > 0.0 ? (1.0 / bg_alpha) : 1.0
    );
    rgba *= effective_opacity;

    // Blend image on top of a fully opaque version of the background color.
    rgba += max(
        float4(0.0, 0.0, 0.0, 0.0),
        float4(input.bg_color.rgb, 1.0) * (1.0 - rgba.a)
    );

    // Multiply everything by the background color alpha.
    rgba *= bg_alpha;

    return rgba;
}
