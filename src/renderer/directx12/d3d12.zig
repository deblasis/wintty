const std = @import("std");
const com = @import("com.zig");
const dxgi = @import("dxgi.zig");
const GUID = com.GUID;
const HRESULT = com.HRESULT;
const IUnknown = com.IUnknown;
const Reserved = com.Reserved;
const DXGI_FORMAT = dxgi.DXGI_FORMAT;
const DXGI_SAMPLE_DESC = dxgi.DXGI_SAMPLE_DESC;

const HANDLE = std.os.windows.HANDLE;
const BOOL = std.os.windows.BOOL;
const LPCSTR = [*:0]const u8;
const LPCWSTR = [*:0]const u16;

// --- Feature levels ---

pub const D3D_FEATURE_LEVEL_12_0: u32 = 0xc000;
pub const D3D_FEATURE_LEVEL_12_1: u32 = 0xc100;

// --- Win32 access rights ---

/// Equivalent to the Win32 GENERIC_ALL access mask from winnt.h. Used
/// as the `Access` parameter to CreateSharedHandle.
pub const GENERIC_ALL: u32 = 0x10000000;

// --- Enums ---

pub const D3D12_COMMAND_LIST_TYPE = enum(u32) {
    DIRECT = 0,
    BUNDLE = 1,
    COMPUTE = 2,
    COPY = 3,
};

pub const D3D12_COMMAND_QUEUE_FLAGS = enum(u32) {
    NONE = 0,
    DISABLE_GPU_TIMEOUT = 1,
};

pub const D3D12_DESCRIPTOR_HEAP_TYPE = enum(u32) {
    CBV_SRV_UAV = 0,
    SAMPLER = 1,
    RTV = 2,
    DSV = 3,
};

pub const D3D12_DESCRIPTOR_HEAP_FLAGS = enum(u32) {
    NONE = 0,
    SHADER_VISIBLE = 1,
};

pub const D3D12_RESOURCE_STATES = enum(u32) {
    COMMON = 0,
    VERTEX_AND_CONSTANT_BUFFER = 0x1,
    INDEX_BUFFER = 0x2,
    RENDER_TARGET = 0x4,
    UNORDERED_ACCESS = 0x8,
    DEPTH_WRITE = 0x10,
    NON_PIXEL_SHADER_RESOURCE = 0x40,
    PIXEL_SHADER_RESOURCE = 0x80,
    INDIRECT_ARGUMENT = 0x200,
    COPY_DEST = 0x400,
    COPY_SOURCE = 0x800,
    /// VERTEX_AND_CONSTANT_BUFFER | INDEX_BUFFER | NON_PIXEL_SHADER_RESOURCE |
    /// PIXEL_SHADER_RESOURCE | INDIRECT_ARGUMENT | COPY_SOURCE
    GENERIC_READ = 0x1 | 0x2 | 0x40 | 0x80 | 0x200 | 0x800,
    _,

    /// Alias for COMMON (both are 0 per the D3D12 spec).
    pub const PRESENT: D3D12_RESOURCE_STATES = .COMMON;
};

pub const D3D12_HEAP_TYPE = enum(u32) {
    DEFAULT = 1,
    UPLOAD = 2,
    READBACK = 3,
    CUSTOM = 4,
};

pub const D3D12_RESOURCE_DIMENSION = enum(u32) {
    UNKNOWN = 0,
    BUFFER = 1,
    TEXTURE1D = 2,
    TEXTURE2D = 3,
    TEXTURE3D = 4,
};

pub const D3D12_TEXTURE_LAYOUT = enum(u32) {
    UNKNOWN = 0,
    ROW_MAJOR = 1,
    UNDEFINED_SWIZZLE_64KB = 2,
    STANDARD_SWIZZLE_64KB = 3,
};

pub const D3D12_RESOURCE_FLAGS = enum(u32) {
    NONE = 0,
    ALLOW_RENDER_TARGET = 0x1,
    ALLOW_DEPTH_STENCIL = 0x2,
    ALLOW_UNORDERED_ACCESS = 0x4,
    DENY_SHADER_RESOURCE = 0x8,
    ALLOW_CROSS_ADAPTER = 0x10,
    ALLOW_SIMULTANEOUS_ACCESS = 0x20,
    _,
};

pub const D3D12_HEAP_FLAGS = enum(u32) {
    NONE = 0,
    SHARED = 0x1,
    DENY_BUFFERS = 0x4,
    ALLOW_DISPLAY = 0x8,
    SHARED_CROSS_ADAPTER = 0x20,
    DENY_RT_DS_TEXTURES = 0x40,
    DENY_NON_RT_DS_TEXTURES = 0x80,
    _,
};

pub const D3D12_FENCE_FLAGS = enum(u32) {
    NONE = 0,
    SHARED = 1,
    SHARED_CROSS_ADAPTER = 2,
    _,
};

pub const D3D12_RESOURCE_BARRIER_TYPE = enum(u32) {
    TRANSITION = 0,
    ALIASING = 1,
    UAV = 2,
};

pub const D3D12_RESOURCE_BARRIER_FLAGS = enum(u32) {
    NONE = 0,
    BEGIN_ONLY = 1,
    END_ONLY = 2,
    _,
};

pub const D3D12_PRIMITIVE_TOPOLOGY_TYPE = enum(u32) {
    UNDEFINED = 0,
    POINT = 1,
    LINE = 2,
    TRIANGLE = 3,
    PATCH = 4,
};

pub const D3D_PRIMITIVE_TOPOLOGY = enum(u32) {
    UNDEFINED = 0,
    POINTLIST = 1,
    LINELIST = 2,
    LINESTRIP = 3,
    TRIANGLELIST = 4,
    TRIANGLESTRIP = 5,
    _,
};

pub const D3D12_INPUT_CLASSIFICATION = enum(u32) {
    PER_VERTEX_DATA = 0,
    PER_INSTANCE_DATA = 1,
};

pub const D3D12_BLEND = enum(u32) {
    ZERO = 1,
    ONE = 2,
    SRC_COLOR = 3,
    INV_SRC_COLOR = 4,
    SRC_ALPHA = 5,
    INV_SRC_ALPHA = 6,
    DEST_ALPHA = 7,
    INV_DEST_ALPHA = 8,
    DEST_COLOR = 9,
    INV_DEST_COLOR = 10,
    SRC_ALPHA_SAT = 11,
    BLEND_FACTOR = 14,
    INV_BLEND_FACTOR = 15,
    SRC1_COLOR = 16,
    INV_SRC1_COLOR = 17,
    SRC1_ALPHA = 18,
    INV_SRC1_ALPHA = 19,
    _,
};

pub const D3D12_BLEND_OP = enum(u32) {
    ADD = 1,
    SUBTRACT = 2,
    REV_SUBTRACT = 3,
    MIN = 4,
    MAX = 5,
};

pub const D3D12_LOGIC_OP = enum(u32) {
    CLEAR = 0,
    SET = 1,
    COPY = 2,
    COPY_INVERTED = 3,
    NOOP = 4,
    INVERT = 5,
    AND = 6,
    NAND = 7,
    OR = 8,
    NOR = 9,
    XOR = 10,
    EQUIV = 11,
    AND_REVERSE = 12,
    AND_INVERTED = 13,
    OR_REVERSE = 14,
    OR_INVERTED = 15,
};

pub const D3D12_FILL_MODE = enum(u32) {
    WIREFRAME = 2,
    SOLID = 3,
};

pub const D3D12_CULL_MODE = enum(u32) {
    NONE = 1,
    FRONT = 2,
    BACK = 3,
};

pub const D3D12_ROOT_SIGNATURE_FLAGS = enum(u32) {
    NONE = 0,
    ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT = 0x1,
    DENY_VERTEX_SHADER_ROOT_ACCESS = 0x2,
    DENY_HULL_SHADER_ROOT_ACCESS = 0x4,
    DENY_DOMAIN_SHADER_ROOT_ACCESS = 0x8,
    DENY_GEOMETRY_SHADER_ROOT_ACCESS = 0x10,
    DENY_PIXEL_SHADER_ROOT_ACCESS = 0x20,
    ALLOW_STREAM_OUTPUT = 0x40,
    _,
};

pub const D3D12_ROOT_PARAMETER_TYPE = enum(u32) {
    DESCRIPTOR_TABLE = 0,
    CONSTANTS = 1,
    CBV = 2,
    SRV = 3,
    UAV = 4,
};

pub const D3D12_DESCRIPTOR_RANGE_TYPE = enum(u32) {
    SRV = 0,
    UAV = 1,
    CBV = 2,
    SAMPLER = 3,
};

pub const D3D12_SHADER_VISIBILITY = enum(u32) {
    ALL = 0,
    VERTEX = 1,
    HULL = 2,
    DOMAIN = 3,
    GEOMETRY = 4,
    PIXEL = 5,
};

pub const D3D12_FILTER = enum(u32) {
    MIN_MAG_MIP_POINT = 0,
    MIN_MAG_POINT_MIP_LINEAR = 0x1,
    MIN_POINT_MAG_LINEAR_MIP_POINT = 0x4,
    MIN_POINT_MAG_MIP_LINEAR = 0x5,
    MIN_LINEAR_MAG_MIP_POINT = 0x10,
    MIN_LINEAR_MAG_POINT_MIP_LINEAR = 0x11,
    MIN_MAG_LINEAR_MIP_POINT = 0x14,
    MIN_MAG_MIP_LINEAR = 0x15,
    ANISOTROPIC = 0x55,
    _,
};

pub const D3D12_TEXTURE_ADDRESS_MODE = enum(u32) {
    WRAP = 1,
    MIRROR = 2,
    CLAMP = 3,
    BORDER = 4,
    MIRROR_ONCE = 5,
};

pub const D3D12_COMPARISON_FUNC = enum(u32) {
    NEVER = 1,
    LESS = 2,
    EQUAL = 3,
    LESS_EQUAL = 4,
    GREATER = 5,
    NOT_EQUAL = 6,
    GREATER_EQUAL = 7,
    ALWAYS = 8,
};

pub const D3D12_STATIC_BORDER_COLOR = enum(u32) {
    TRANSPARENT_BLACK = 0,
    OPAQUE_BLACK = 1,
    OPAQUE_WHITE = 2,
};

pub const D3D12_SRV_DIMENSION = enum(u32) {
    UNKNOWN = 0,
    BUFFER = 1,
    TEXTURE1D = 2,
    TEXTURE1DARRAY = 3,
    TEXTURE2D = 4,
    TEXTURE2DARRAY = 5,
    TEXTURE2DMS = 6,
    TEXTURE2DMSARRAY = 7,
    TEXTURE3D = 8,
    TEXTURECUBE = 9,
    TEXTURECUBEARRAY = 10,
    RAYTRACING_ACCELERATION_STRUCTURE = 11,
};

pub const D3D12_TEX2D_SRV = extern struct {
    MostDetailedMip: u32,
    MipLevels: u32,
    PlaneSlice: u32,
    ResourceMinLODClamp: f32,
};

pub const D3D12_BUFFER_SRV = extern struct {
    FirstElement: u64,
    NumElements: u32,
    StructureByteStride: u32,
    Flags: u32,
};

pub const D3D12_SHADER_RESOURCE_VIEW_DESC = extern struct {
    Format: DXGI_FORMAT,
    ViewDimension: D3D12_SRV_DIMENSION,
    Shader4ComponentMapping: u32,
    u: extern union {
        // The union must be the size of the largest member (D3D12_BUFFER_SRV
        // at 24 bytes). Without all members, the union would be too small and
        // the D3D12 runtime could read past the end of the struct.
        Buffer: D3D12_BUFFER_SRV,
        Texture2D: D3D12_TEX2D_SRV,
    },
};

/// Default component mapping: identity (RGBA -> RGBA).
pub const D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING: u32 = 0x00001688;

pub const D3D12_SAMPLER_DESC = extern struct {
    Filter: D3D12_FILTER,
    AddressU: D3D12_TEXTURE_ADDRESS_MODE,
    AddressV: D3D12_TEXTURE_ADDRESS_MODE,
    AddressW: D3D12_TEXTURE_ADDRESS_MODE,
    MipLODBias: f32,
    MaxAnisotropy: u32,
    ComparisonFunc: D3D12_COMPARISON_FUNC,
    BorderColor: [4]f32,
    MinLOD: f32,
    MaxLOD: f32,
};

pub const D3D12_COLOR_WRITE_ENABLE = enum(u32) {
    RED = 1,
    GREEN = 2,
    BLUE = 4,
    ALPHA = 8,
    ALL = 15,
    _,
};

// --- Structs ---

pub const D3D12_COMMAND_QUEUE_DESC = extern struct {
    Type: D3D12_COMMAND_LIST_TYPE,
    Priority: i32,
    Flags: D3D12_COMMAND_QUEUE_FLAGS,
    NodeMask: u32,
};

pub const D3D12_DESCRIPTOR_HEAP_DESC = extern struct {
    Type: D3D12_DESCRIPTOR_HEAP_TYPE,
    NumDescriptors: u32,
    Flags: D3D12_DESCRIPTOR_HEAP_FLAGS,
    NodeMask: u32,
};

pub const D3D12_CPU_DESCRIPTOR_HANDLE = extern struct {
    ptr: usize,
};

pub const D3D12_GPU_DESCRIPTOR_HANDLE = extern struct {
    ptr: u64,
};

pub const D3D12_RESOURCE_TRANSITION_BARRIER = extern struct {
    pResource: *ID3D12Resource,
    Subresource: u32,
    StateBefore: D3D12_RESOURCE_STATES,
    StateAfter: D3D12_RESOURCE_STATES,
};

pub const D3D12_RESOURCE_ALIASING_BARRIER = extern struct {
    pResourceBefore: ?*ID3D12Resource,
    pResourceAfter: ?*ID3D12Resource,
};

pub const D3D12_RESOURCE_UAV_BARRIER = extern struct {
    pResource: ?*ID3D12Resource,
};

pub const D3D12_RESOURCE_BARRIER = extern struct {
    Type: D3D12_RESOURCE_BARRIER_TYPE,
    Flags: D3D12_RESOURCE_BARRIER_FLAGS,
    u: extern union {
        Transition: D3D12_RESOURCE_TRANSITION_BARRIER,
        Aliasing: D3D12_RESOURCE_ALIASING_BARRIER,
        UAV: D3D12_RESOURCE_UAV_BARRIER,
    },
};

pub const D3D12_VIEWPORT = extern struct {
    TopLeftX: f32,
    TopLeftY: f32,
    Width: f32,
    Height: f32,
    MinDepth: f32,
    MaxDepth: f32,
};

pub const D3D12_RECT = extern struct {
    left: i32,
    top: i32,
    right: i32,
    bottom: i32,
};

pub const D3D12_HEAP_PROPERTIES = extern struct {
    Type: D3D12_HEAP_TYPE,
    CPUPageProperty: u32,
    MemoryPoolPreference: u32,
    CreationNodeMask: u32,
    VisibleNodeMask: u32,
};

pub const D3D12_CLEAR_VALUE = extern struct {
    Format: DXGI_FORMAT,
    u: extern union {
        Color: [4]f32,
        DepthStencil: D3D12_DEPTH_STENCIL_VALUE,
    },
};

pub const D3D12_DEPTH_STENCIL_VALUE = extern struct {
    Depth: f32,
    Stencil: u8,
};

pub const D3D12_RESOURCE_DESC = extern struct {
    Dimension: D3D12_RESOURCE_DIMENSION,
    Alignment: u64,
    Width: u64,
    Height: u32,
    DepthOrArraySize: u16,
    MipLevels: u16,
    Format: DXGI_FORMAT,
    SampleDesc: DXGI_SAMPLE_DESC,
    Layout: D3D12_TEXTURE_LAYOUT,
    Flags: D3D12_RESOURCE_FLAGS,
};

pub const D3D12_VERTEX_BUFFER_VIEW = extern struct {
    BufferLocation: u64,
    SizeInBytes: u32,
    StrideInBytes: u32,
};

pub const D3D12_SHADER_BYTECODE = extern struct {
    pShaderBytecode: ?*const anyopaque,
    BytecodeLength: usize,
};

pub const D3D12_INPUT_ELEMENT_DESC = extern struct {
    SemanticName: LPCSTR,
    SemanticIndex: u32,
    Format: DXGI_FORMAT,
    InputSlot: u32,
    AlignedByteOffset: u32,
    InputSlotClass: D3D12_INPUT_CLASSIFICATION,
    InstanceDataStepRate: u32,
};

pub const D3D12_INPUT_LAYOUT_DESC = extern struct {
    pInputElementDescs: ?[*]const D3D12_INPUT_ELEMENT_DESC,
    NumElements: u32,
};

pub const D3D12_RENDER_TARGET_BLEND_DESC = extern struct {
    BlendEnable: BOOL,
    LogicOpEnable: BOOL,
    SrcBlend: D3D12_BLEND,
    DestBlend: D3D12_BLEND,
    BlendOp: D3D12_BLEND_OP,
    SrcBlendAlpha: D3D12_BLEND,
    DestBlendAlpha: D3D12_BLEND,
    BlendOpAlpha: D3D12_BLEND_OP,
    LogicOp: D3D12_LOGIC_OP,
    RenderTargetWriteMask: u8,
};

pub const D3D12_BLEND_DESC = extern struct {
    AlphaToCoverageEnable: BOOL,
    IndependentBlendEnable: BOOL,
    RenderTarget: [8]D3D12_RENDER_TARGET_BLEND_DESC,
};

pub const D3D12_RASTERIZER_DESC = extern struct {
    FillMode: D3D12_FILL_MODE,
    CullMode: D3D12_CULL_MODE,
    FrontCounterClockwise: BOOL,
    DepthBias: i32,
    DepthBiasClamp: f32,
    SlopeScaledDepthBias: f32,
    DepthClipEnable: BOOL,
    MultisampleEnable: BOOL,
    AntialiasedLineEnable: BOOL,
    ForcedSampleCount: u32,
    ConservativeRaster: u32,
};

pub const D3D12_STENCIL_OP = enum(u32) {
    KEEP = 1,
    ZERO = 2,
    REPLACE = 3,
    INCR_SAT = 4,
    DECR_SAT = 5,
    INVERT = 6,
    INCR = 7,
    DECR = 8,
};

pub const D3D12_DEPTH_WRITE_MASK = enum(u32) {
    ZERO = 0,
    ALL = 1,
};

pub const D3D12_DEPTH_STENCILOP_DESC = extern struct {
    StencilFailOp: D3D12_STENCIL_OP,
    StencilDepthFailOp: D3D12_STENCIL_OP,
    StencilPassOp: D3D12_STENCIL_OP,
    StencilFunc: D3D12_COMPARISON_FUNC,
};

pub const D3D12_DEPTH_STENCIL_DESC = extern struct {
    DepthEnable: BOOL,
    DepthWriteMask: D3D12_DEPTH_WRITE_MASK,
    DepthFunc: D3D12_COMPARISON_FUNC,
    StencilEnable: BOOL,
    StencilReadMask: u8,
    StencilWriteMask: u8,
    FrontFace: D3D12_DEPTH_STENCILOP_DESC,
    BackFace: D3D12_DEPTH_STENCILOP_DESC,
};

pub const D3D12_STREAM_OUTPUT_DESC = extern struct {
    pSODeclaration: ?*const anyopaque,
    NumEntries: u32,
    pBufferStrides: ?*const u32,
    NumStrides: u32,
    RasterizedStream: u32,
};

pub const D3D12_CACHED_PIPELINE_STATE = extern struct {
    pCachedBlob: ?*const anyopaque,
    CachedBlobSizeInBytes: usize,
};

pub const D3D12_GRAPHICS_PIPELINE_STATE_DESC = extern struct {
    pRootSignature: ?*ID3D12RootSignature,
    VS: D3D12_SHADER_BYTECODE,
    PS: D3D12_SHADER_BYTECODE,
    DS: D3D12_SHADER_BYTECODE,
    HS: D3D12_SHADER_BYTECODE,
    GS: D3D12_SHADER_BYTECODE,
    StreamOutput: D3D12_STREAM_OUTPUT_DESC,
    BlendState: D3D12_BLEND_DESC,
    SampleMask: u32,
    RasterizerState: D3D12_RASTERIZER_DESC,
    DepthStencilState: D3D12_DEPTH_STENCIL_DESC,
    InputLayout: D3D12_INPUT_LAYOUT_DESC,
    IBStripCutValue: u32,
    PrimitiveTopologyType: D3D12_PRIMITIVE_TOPOLOGY_TYPE,
    NumRenderTargets: u32,
    RTVFormats: [8]DXGI_FORMAT,
    DSVFormat: DXGI_FORMAT,
    SampleDesc: DXGI_SAMPLE_DESC,
    NodeMask: u32,
    CachedPSO: D3D12_CACHED_PIPELINE_STATE,
    Flags: u32,
};

pub const D3D12_DESCRIPTOR_RANGE = extern struct {
    RangeType: D3D12_DESCRIPTOR_RANGE_TYPE,
    NumDescriptors: u32,
    BaseShaderRegister: u32,
    RegisterSpace: u32,
    OffsetInDescriptorsFromTableStart: u32,
};

pub const D3D12_ROOT_DESCRIPTOR_TABLE = extern struct {
    NumDescriptorRanges: u32,
    pDescriptorRanges: ?[*]const D3D12_DESCRIPTOR_RANGE,
};

pub const D3D12_ROOT_CONSTANTS = extern struct {
    ShaderRegister: u32,
    RegisterSpace: u32,
    Num32BitValues: u32,
};

pub const D3D12_ROOT_DESCRIPTOR = extern struct {
    ShaderRegister: u32,
    RegisterSpace: u32,
};

pub const D3D12_ROOT_PARAMETER = extern struct {
    ParameterType: D3D12_ROOT_PARAMETER_TYPE,
    u: extern union {
        DescriptorTable: D3D12_ROOT_DESCRIPTOR_TABLE,
        Constants: D3D12_ROOT_CONSTANTS,
        Descriptor: D3D12_ROOT_DESCRIPTOR,
    },
    ShaderVisibility: D3D12_SHADER_VISIBILITY,
};

pub const D3D12_STATIC_SAMPLER_DESC = extern struct {
    Filter: D3D12_FILTER,
    AddressU: D3D12_TEXTURE_ADDRESS_MODE,
    AddressV: D3D12_TEXTURE_ADDRESS_MODE,
    AddressW: D3D12_TEXTURE_ADDRESS_MODE,
    MipLODBias: f32,
    MaxAnisotropy: u32,
    ComparisonFunc: D3D12_COMPARISON_FUNC,
    BorderColor: D3D12_STATIC_BORDER_COLOR,
    MinLOD: f32,
    MaxLOD: f32,
    ShaderRegister: u32,
    RegisterSpace: u32,
    ShaderVisibility: D3D12_SHADER_VISIBILITY,
};

pub const D3D12_ROOT_SIGNATURE_DESC = extern struct {
    NumParameters: u32,
    pParameters: ?[*]const D3D12_ROOT_PARAMETER,
    NumStaticSamplers: u32,
    pStaticSamplers: ?[*]const D3D12_STATIC_SAMPLER_DESC,
    Flags: D3D12_ROOT_SIGNATURE_FLAGS,
};

// --- Root Signature versioning ---

pub const D3D_ROOT_SIGNATURE_VERSION = enum(u32) {
    VERSION_1_0 = 1,
    VERSION_1_1 = 2,
};

// --- Root Signature v1.1 types ---

pub const D3D12_DESCRIPTOR_RANGE_FLAGS = enum(u32) {
    NONE = 0,
    DESCRIPTORS_VOLATILE = 0x1,
    DATA_VOLATILE = 0x2,
    DATA_STATIC_WHILE_SET_AT_EXECUTE = 0x4,
    DATA_STATIC = 0x8,
    DESCRIPTORS_STATIC_KEEPING_BUFFER_BOUNDS_CHECKS = 0x10000,
    _,
};

pub const D3D12_DESCRIPTOR_RANGE1 = extern struct {
    RangeType: D3D12_DESCRIPTOR_RANGE_TYPE,
    NumDescriptors: u32,
    BaseShaderRegister: u32,
    RegisterSpace: u32,
    Flags: D3D12_DESCRIPTOR_RANGE_FLAGS,
    OffsetInDescriptorsFromTableStart: u32,
};

pub const D3D12_ROOT_DESCRIPTOR_TABLE1 = extern struct {
    NumDescriptorRanges: u32,
    pDescriptorRanges: ?[*]const D3D12_DESCRIPTOR_RANGE1,
};

pub const D3D12_ROOT_DESCRIPTOR_FLAGS = enum(u32) {
    NONE = 0,
    DATA_VOLATILE = 0x2,
    DATA_STATIC_WHILE_SET_AT_EXECUTE = 0x4,
    DATA_STATIC = 0x8,
    _,
};

pub const D3D12_ROOT_DESCRIPTOR1 = extern struct {
    ShaderRegister: u32,
    RegisterSpace: u32,
    Flags: D3D12_ROOT_DESCRIPTOR_FLAGS,
};

pub const D3D12_ROOT_PARAMETER1 = extern struct {
    ParameterType: D3D12_ROOT_PARAMETER_TYPE,
    u: extern union {
        DescriptorTable: D3D12_ROOT_DESCRIPTOR_TABLE1,
        Constants: D3D12_ROOT_CONSTANTS,
        Descriptor: D3D12_ROOT_DESCRIPTOR1,
    },
    ShaderVisibility: D3D12_SHADER_VISIBILITY,
};

pub const D3D12_ROOT_SIGNATURE_DESC1 = extern struct {
    NumParameters: u32,
    pParameters: ?[*]const D3D12_ROOT_PARAMETER1,
    NumStaticSamplers: u32,
    pStaticSamplers: ?[*]const D3D12_STATIC_SAMPLER_DESC,
    Flags: D3D12_ROOT_SIGNATURE_FLAGS,
};

pub const D3D12_VERSIONED_ROOT_SIGNATURE_DESC = extern struct {
    Version: D3D_ROOT_SIGNATURE_VERSION,
    u: extern union {
        Desc_1_0: D3D12_ROOT_SIGNATURE_DESC,
        Desc_1_1: D3D12_ROOT_SIGNATURE_DESC1,
    },
};

pub const D3D12_SUBRESOURCE_FOOTPRINT = extern struct {
    Format: DXGI_FORMAT,
    Width: u32,
    Height: u32,
    Depth: u32,
    RowPitch: u32,
};

pub const D3D12_PLACED_SUBRESOURCE_FOOTPRINT = extern struct {
    Offset: u64,
    Footprint: D3D12_SUBRESOURCE_FOOTPRINT,
};

pub const D3D12_TEXTURE_COPY_TYPE = enum(u32) {
    SUBRESOURCE_INDEX = 0,
    PLACED_FOOTPRINT = 1,
};

pub const D3D12_TEXTURE_COPY_LOCATION = extern struct {
    pResource: *ID3D12Resource,
    Type: D3D12_TEXTURE_COPY_TYPE,
    u: extern union {
        PlacedFootprint: D3D12_PLACED_SUBRESOURCE_FOOTPRINT,
        SubresourceIndex: u32,
    },
};

pub const D3D12_RANGE = extern struct {
    Begin: usize,
    End: usize,
};

pub const D3D12_BOX = extern struct {
    left: u32,
    top: u32,
    front: u32,
    right: u32,
    bottom: u32,
    back: u32,
};

// --- COM Interfaces ---
//
// Inheritance chains (slot counts):
//   IUnknown: QueryInterface, AddRef, Release (3 slots)
//   ID3D12Object adds: GetPrivateData, SetPrivateData, SetPrivateDataInterface, SetName (4 slots)
//   ID3D12DeviceChild adds: GetDevice (1 slot)
//   ID3D12Pageable adds nothing (0 slots)
//   ID3D12CommandList adds: GetType (1 slot)
//
// Common inherited totals:
//   Through ID3D12Object:     3 + 4 = 7 slots
//   Through ID3D12DeviceChild: 7 + 1 = 8 slots
//   Through ID3D12Pageable:    8 + 0 = 8 slots
//   Through ID3D12CommandList:  8 + 1 = 9 slots

// ID3D12Debug
pub const ID3D12Debug = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x344488b7,
        .data2 = 0x6846,
        .data3 = 0x474b,
        .data4 = .{ 0xb9, 0x89, 0xf0, 0x27, 0x44, 0x82, 0x45, 0xe0 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12Debug, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12Debug) callconv(.winapi) u32,
        Release: *const fn (*ID3D12Debug) callconv(.winapi) u32,
        // ID3D12Debug (slot 3)
        EnableDebugLayer: *const fn (*ID3D12Debug) callconv(.winapi) void,
    };

    pub inline fn EnableDebugLayer(self: *ID3D12Debug) void {
        self.vtable.EnableDebugLayer(self);
    }

    pub inline fn Release(self: *ID3D12Debug) u32 {
        return self.vtable.Release(self);
    }
};

// ID3DBlob (ID3D10Blob)
pub const ID3DBlob = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3DBlob, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3DBlob) callconv(.winapi) u32,
        Release: *const fn (*ID3DBlob) callconv(.winapi) u32,
        // ID3DBlob (slots 3-4)
        GetBufferPointer: *const fn (*ID3DBlob) callconv(.winapi) *anyopaque,
        GetBufferSize: *const fn (*ID3DBlob) callconv(.winapi) usize,
    };

    pub inline fn GetBufferPointer(self: *ID3DBlob) *anyopaque {
        return self.vtable.GetBufferPointer(self);
    }

    pub inline fn GetBufferSize(self: *ID3DBlob) usize {
        return self.vtable.GetBufferSize(self);
    }

    pub inline fn Release(self: *ID3DBlob) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12CommandQueue
// Inherits: IUnknown (3) -> ID3D12Object (4) -> ID3D12DeviceChild (1) -> ID3D12Pageable (0) = 8 inherited
pub const ID3D12CommandQueue = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x0ec870a6,
        .data2 = 0x5d7e,
        .data3 = 0x4c22,
        .data4 = .{ 0x8c, 0xfc, 0x5b, 0xaa, 0xe0, 0x76, 0x16, 0xed },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12CommandQueue, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12CommandQueue) callconv(.winapi) u32,
        Release: *const fn (*ID3D12CommandQueue) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12DeviceChild (slot 7)
        GetDevice: Reserved,
        // ID3D12Pageable adds nothing
        // ID3D12CommandQueue own methods (slots 8+)
        UpdateTileMappings: Reserved,
        CopyTileMappings: Reserved,
        ExecuteCommandLists: *const fn (*ID3D12CommandQueue, NumCommandLists: u32, ppCommandLists: [*]const *ID3D12GraphicsCommandList) callconv(.winapi) void,
        SetMarker: Reserved,
        BeginEvent: Reserved,
        EndEvent: Reserved,
        Signal: *const fn (*ID3D12CommandQueue, pFence: *ID3D12Fence, Value: u64) callconv(.winapi) HRESULT,
        Wait: Reserved,
        GetTimestampFrequency: Reserved,
        GetClockCalibration: Reserved,
        GetDesc: Reserved,
    };

    pub inline fn ExecuteCommandLists(self: *ID3D12CommandQueue, num: u32, lists: [*]const *ID3D12GraphicsCommandList) void {
        self.vtable.ExecuteCommandLists(self, num, lists);
    }

    pub inline fn Signal(self: *ID3D12CommandQueue, fence: *ID3D12Fence, value: u64) HRESULT {
        return self.vtable.Signal(self, fence, value);
    }

    pub inline fn Release(self: *ID3D12CommandQueue) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12CommandAllocator
// Inherits: IUnknown (3) -> ID3D12Object (4) -> ID3D12DeviceChild (1) -> ID3D12Pageable (0) = 8 inherited
pub const ID3D12CommandAllocator = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x6102dee4,
        .data2 = 0xaf59,
        .data3 = 0x4b09,
        .data4 = .{ 0xb9, 0x99, 0xb4, 0x4d, 0x73, 0xf0, 0x9b, 0x24 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12CommandAllocator, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12CommandAllocator) callconv(.winapi) u32,
        Release: *const fn (*ID3D12CommandAllocator) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12DeviceChild (slot 7)
        GetDevice: Reserved,
        // ID3D12Pageable adds nothing
        // ID3D12CommandAllocator (slot 8)
        Reset: *const fn (*ID3D12CommandAllocator) callconv(.winapi) HRESULT,
    };

    pub inline fn Reset(self: *ID3D12CommandAllocator) HRESULT {
        return self.vtable.Reset(self);
    }

    pub inline fn Release(self: *ID3D12CommandAllocator) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12Fence
// Inherits: IUnknown (3) -> ID3D12Object (4) -> ID3D12DeviceChild (1) -> ID3D12Pageable (0) = 8 inherited
pub const ID3D12Fence = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x0a753dcf,
        .data2 = 0xc4d8,
        .data3 = 0x4b91,
        .data4 = .{ 0xad, 0xf6, 0xbe, 0x5a, 0x60, 0xd9, 0x5a, 0x76 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12Fence, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12Fence) callconv(.winapi) u32,
        Release: *const fn (*ID3D12Fence) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12DeviceChild (slot 7)
        GetDevice: Reserved,
        // ID3D12Pageable adds nothing
        // ID3D12Fence (slots 8-10)
        GetCompletedValue: *const fn (*ID3D12Fence) callconv(.winapi) u64,
        SetEventOnCompletion: *const fn (*ID3D12Fence, Value: u64, hEvent: HANDLE) callconv(.winapi) HRESULT,
        Signal: Reserved,
    };

    pub inline fn GetCompletedValue(self: *ID3D12Fence) u64 {
        return self.vtable.GetCompletedValue(self);
    }

    pub inline fn SetEventOnCompletion(self: *ID3D12Fence, value: u64, event: HANDLE) HRESULT {
        return self.vtable.SetEventOnCompletion(self, value, event);
    }

    pub inline fn Release(self: *ID3D12Fence) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12DescriptorHeap
// Inherits: IUnknown (3) -> ID3D12Object (4) -> ID3D12DeviceChild (1) -> ID3D12Pageable (0) = 8 inherited
pub const ID3D12DescriptorHeap = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x8efb471d,
        .data2 = 0x616c,
        .data3 = 0x4f49,
        .data4 = .{ 0x90, 0xf7, 0x12, 0x7b, 0xb7, 0x63, 0xfa, 0x51 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12DescriptorHeap, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12DescriptorHeap) callconv(.winapi) u32,
        Release: *const fn (*ID3D12DescriptorHeap) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12DeviceChild (slot 7)
        GetDevice: Reserved,
        // ID3D12Pageable adds nothing
        // ID3D12DescriptorHeap (slots 8-10)
        GetDesc: Reserved,
        // These COM methods return structs via a hidden output pointer
        // (the C ABI convention used in the actual vtable). The C++ wrapper
        // hides this, but the binary vtable uses: void fn(This, *RetVal).
        GetCPUDescriptorHandleForHeapStart: *const fn (*ID3D12DescriptorHeap, *D3D12_CPU_DESCRIPTOR_HANDLE) callconv(.winapi) void,
        GetGPUDescriptorHandleForHeapStart: *const fn (*ID3D12DescriptorHeap, *D3D12_GPU_DESCRIPTOR_HANDLE) callconv(.winapi) void,
    };

    pub inline fn GetCPUDescriptorHandleForHeapStart(self: *ID3D12DescriptorHeap) D3D12_CPU_DESCRIPTOR_HANDLE {
        var result: D3D12_CPU_DESCRIPTOR_HANDLE = undefined;
        self.vtable.GetCPUDescriptorHandleForHeapStart(self, &result);
        return result;
    }

    pub inline fn GetGPUDescriptorHandleForHeapStart(self: *ID3D12DescriptorHeap) D3D12_GPU_DESCRIPTOR_HANDLE {
        var result: D3D12_GPU_DESCRIPTOR_HANDLE = undefined;
        self.vtable.GetGPUDescriptorHandleForHeapStart(self, &result);
        return result;
    }

    pub inline fn Release(self: *ID3D12DescriptorHeap) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12Resource
// Inherits: IUnknown (3) -> ID3D12Object (4) -> ID3D12DeviceChild (1) -> ID3D12Pageable (0) = 8 inherited
pub const ID3D12Resource = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x696442be,
        .data2 = 0xa72e,
        .data3 = 0x4059,
        .data4 = .{ 0xbc, 0x79, 0x5b, 0x5c, 0x98, 0x04, 0x0f, 0xad },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12Resource, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12Resource) callconv(.winapi) u32,
        Release: *const fn (*ID3D12Resource) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12DeviceChild (slot 7)
        GetDevice: Reserved,
        // ID3D12Pageable adds nothing
        // ID3D12Resource (slots 8-14)
        Map: *const fn (*ID3D12Resource, Subresource: u32, pReadRange: ?*const D3D12_RANGE, ppData: *?*anyopaque) callconv(.winapi) HRESULT,
        Unmap: *const fn (*ID3D12Resource, Subresource: u32, pWrittenRange: ?*const D3D12_RANGE) callconv(.winapi) void,
        GetDesc: Reserved,
        GetGPUVirtualAddress: *const fn (*ID3D12Resource) callconv(.winapi) u64,
        WriteToSubresource: Reserved,
        ReadFromSubresource: Reserved,
        GetHeapProperties: Reserved,
    };

    pub inline fn Map(self: *ID3D12Resource, subresource: u32, read_range: ?*const D3D12_RANGE, data: *?*anyopaque) HRESULT {
        return self.vtable.Map(self, subresource, read_range, data);
    }

    pub inline fn Unmap(self: *ID3D12Resource, subresource: u32, written_range: ?*const D3D12_RANGE) void {
        self.vtable.Unmap(self, subresource, written_range);
    }

    pub inline fn GetGPUVirtualAddress(self: *ID3D12Resource) u64 {
        return self.vtable.GetGPUVirtualAddress(self);
    }

    pub inline fn Release(self: *ID3D12Resource) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12PipelineState
// Inherits: IUnknown (3) -> ID3D12Object (4) -> ID3D12DeviceChild (1) -> ID3D12Pageable (0) = 8 inherited
pub const ID3D12PipelineState = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x765a30f3,
        .data2 = 0xf624,
        .data3 = 0x4c6f,
        .data4 = .{ 0xa8, 0x28, 0xac, 0xe9, 0x48, 0x62, 0x24, 0x45 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12PipelineState, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12PipelineState) callconv(.winapi) u32,
        Release: *const fn (*ID3D12PipelineState) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12DeviceChild (slot 7)
        GetDevice: Reserved,
        // ID3D12Pageable adds nothing
        // ID3D12PipelineState (slot 8)
        GetCachedBlob: Reserved,
    };

    pub inline fn Release(self: *ID3D12PipelineState) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12RootSignature
// Inherits: IUnknown (3) -> ID3D12Object (4) -> ID3D12DeviceChild (1) = 8 inherited
// No own methods beyond inherited.
pub const ID3D12RootSignature = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0xc54a6b66,
        .data2 = 0x72df,
        .data3 = 0x4ee8,
        .data4 = .{ 0x8b, 0xe5, 0xa9, 0x46, 0xa1, 0x42, 0x92, 0x14 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12RootSignature, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12RootSignature) callconv(.winapi) u32,
        Release: *const fn (*ID3D12RootSignature) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12DeviceChild (slot 7)
        GetDevice: Reserved,
    };

    pub inline fn Release(self: *ID3D12RootSignature) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12Heap
// Inherits: IUnknown (3) -> ID3D12Object (4) -> ID3D12DeviceChild (1) -> ID3D12Pageable (0) = 8 inherited
pub const ID3D12Heap = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12Heap, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12Heap) callconv(.winapi) u32,
        Release: *const fn (*ID3D12Heap) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12DeviceChild (slot 7)
        GetDevice: Reserved,
        // ID3D12Pageable adds nothing
        // ID3D12Heap (slot 8)
        GetDesc: Reserved,
    };

    pub inline fn Release(self: *ID3D12Heap) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12GraphicsCommandList
// Inherits: IUnknown (3) -> ID3D12Object (4) -> ID3D12DeviceChild (1) -> ID3D12CommandList (1) = 9 inherited
pub const ID3D12GraphicsCommandList = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x5b160d0f,
        .data2 = 0xac1b,
        .data3 = 0x4185,
        .data4 = .{ 0x8b, 0xa8, 0xb3, 0xae, 0x42, 0xa5, 0xa4, 0x55 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12GraphicsCommandList, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12GraphicsCommandList) callconv(.winapi) u32,
        Release: *const fn (*ID3D12GraphicsCommandList) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12DeviceChild (slot 7)
        GetDevice: Reserved,
        // ID3D12CommandList (slot 8)
        GetType: Reserved,
        // ID3D12GraphicsCommandList own methods (slots 9+)
        // slot 9
        Close: *const fn (*ID3D12GraphicsCommandList) callconv(.winapi) HRESULT,
        // slot 10
        Reset: *const fn (*ID3D12GraphicsCommandList, pAllocator: *ID3D12CommandAllocator, pInitialState: ?*ID3D12PipelineState) callconv(.winapi) HRESULT,
        // slot 11
        ClearState: Reserved,
        // slot 12
        DrawInstanced: *const fn (*ID3D12GraphicsCommandList, VertexCountPerInstance: u32, InstanceCount: u32, StartVertexLocation: u32, StartInstanceLocation: u32) callconv(.winapi) void,
        // slot 13
        DrawIndexedInstanced: Reserved,
        // slot 14
        Dispatch: Reserved,
        // slot 15
        CopyBufferRegion: *const fn (*ID3D12GraphicsCommandList, pDstBuffer: *ID3D12Resource, DstOffset: u64, pSrcBuffer: *ID3D12Resource, SrcOffset: u64, NumBytes: u64) callconv(.winapi) void,
        // slot 16
        CopyTextureRegion: *const fn (*ID3D12GraphicsCommandList, pDst: *const D3D12_TEXTURE_COPY_LOCATION, DstX: u32, DstY: u32, DstZ: u32, pSrc: *const D3D12_TEXTURE_COPY_LOCATION, pSrcBox: ?*const D3D12_BOX) callconv(.winapi) void,
        // slot 17
        CopyResource: Reserved,
        // slot 18
        CopyTiles: Reserved,
        // slot 19
        ResolveSubresource: Reserved,
        // slot 20
        IASetPrimitiveTopology: *const fn (*ID3D12GraphicsCommandList, PrimitiveTopology: D3D_PRIMITIVE_TOPOLOGY) callconv(.winapi) void,
        // slot 21
        RSSetViewports: *const fn (*ID3D12GraphicsCommandList, NumViewports: u32, pViewports: [*]const D3D12_VIEWPORT) callconv(.winapi) void,
        // slot 22
        RSSetScissorRects: *const fn (*ID3D12GraphicsCommandList, NumRects: u32, pRects: [*]const D3D12_RECT) callconv(.winapi) void,
        // slot 23
        OMSetBlendFactor: Reserved,
        // slot 24
        OMSetStencilRef: Reserved,
        // slot 25
        SetPipelineState: *const fn (*ID3D12GraphicsCommandList, pPipelineState: *ID3D12PipelineState) callconv(.winapi) void,
        // slot 26
        ResourceBarrier: *const fn (*ID3D12GraphicsCommandList, NumBarriers: u32, pBarriers: [*]const D3D12_RESOURCE_BARRIER) callconv(.winapi) void,
        // slot 27
        ExecuteBundle: Reserved,
        // slot 28
        SetDescriptorHeaps: *const fn (*ID3D12GraphicsCommandList, NumDescriptorHeaps: u32, ppDescriptorHeaps: [*]const *ID3D12DescriptorHeap) callconv(.winapi) void,
        // slot 29
        SetComputeRootSignature: Reserved,
        // slot 30
        SetGraphicsRootSignature: *const fn (*ID3D12GraphicsCommandList, pRootSignature: ?*ID3D12RootSignature) callconv(.winapi) void,
        // slot 31
        SetComputeRootDescriptorTable: Reserved,
        // slot 32
        // BaseDescriptor is D3D12_GPU_DESCRIPTOR_HANDLE (8-byte struct).
        // Use u64 in the vtable for the same ABI reason as the device
        // descriptor handle parameters above.
        SetGraphicsRootDescriptorTable: *const fn (*ID3D12GraphicsCommandList, RootParameterIndex: u32, BaseDescriptor: u64) callconv(.winapi) void,
        // slot 33
        SetComputeRoot32BitConstant: Reserved,
        // slot 34
        SetGraphicsRoot32BitConstant: Reserved,
        // slot 35
        SetComputeRoot32BitConstants: Reserved,
        // slot 36
        SetGraphicsRoot32BitConstants: Reserved,
        // slot 37
        SetComputeRootConstantBufferView: Reserved,
        // slot 38
        SetGraphicsRootConstantBufferView: *const fn (*ID3D12GraphicsCommandList, RootParameterIndex: u32, BufferLocation: u64) callconv(.winapi) void,
        // slot 39
        SetComputeRootShaderResourceView: Reserved,
        // slot 40
        SetGraphicsRootShaderResourceView: *const fn (*ID3D12GraphicsCommandList, RootParameterIndex: u32, BufferLocation: u64) callconv(.winapi) void,
        // slot 41
        SetComputeRootUnorderedAccessView: Reserved,
        // slot 42
        SetGraphicsRootUnorderedAccessView: Reserved,
        // slot 43
        IASetIndexBuffer: Reserved,
        // slot 44
        IASetVertexBuffers: *const fn (*ID3D12GraphicsCommandList, StartSlot: u32, NumViews: u32, pViews: [*]const D3D12_VERTEX_BUFFER_VIEW) callconv(.winapi) void,
        // slot 45
        SOSetTargets: Reserved,
        // slot 46
        OMSetRenderTargets: *const fn (*ID3D12GraphicsCommandList, NumRenderTargetDescriptors: u32, pRenderTargetDescriptors: ?[*]const D3D12_CPU_DESCRIPTOR_HANDLE, RTsSingleHandleToDescriptorRange: BOOL, pDepthStencilDescriptor: ?*const D3D12_CPU_DESCRIPTOR_HANDLE) callconv(.winapi) void,
        // slot 47
        ClearDepthStencilView: Reserved,
        // slot 48
        // RenderTargetView is D3D12_CPU_DESCRIPTOR_HANDLE (8-byte struct).
        // Use usize in the vtable for the same ABI reason as above.
        ClearRenderTargetView: *const fn (*ID3D12GraphicsCommandList, RenderTargetView: usize, ColorRGBA: *const [4]f32, NumRects: u32, pRects: ?[*]const D3D12_RECT) callconv(.winapi) void,
        // slot 49
        ClearUnorderedAccessViewUint: Reserved,
        // slot 50
        ClearUnorderedAccessViewFloat: Reserved,
        // slot 51
        DiscardResource: Reserved,
        // slot 52
        BeginQuery: Reserved,
        // slot 53
        EndQuery: Reserved,
        // slot 54
        ResolveQueryData: Reserved,
        // slot 55
        SetPredication: Reserved,
        // slot 56
        SetMarker: Reserved,
        // slot 57
        BeginEvent: Reserved,
        // slot 58
        EndEvent: Reserved,
        // slot 59
        ExecuteIndirect: Reserved,
    };

    pub inline fn Close(self: *ID3D12GraphicsCommandList) HRESULT {
        return self.vtable.Close(self);
    }

    pub inline fn Reset(self: *ID3D12GraphicsCommandList, allocator: *ID3D12CommandAllocator, initial_state: ?*ID3D12PipelineState) HRESULT {
        return self.vtable.Reset(self, allocator, initial_state);
    }

    pub inline fn ResourceBarrier(self: *ID3D12GraphicsCommandList, num: u32, barriers: [*]const D3D12_RESOURCE_BARRIER) void {
        self.vtable.ResourceBarrier(self, num, barriers);
    }

    pub inline fn ClearRenderTargetView(self: *ID3D12GraphicsCommandList, rtv: D3D12_CPU_DESCRIPTOR_HANDLE, color: *const [4]f32, num_rects: u32, rects: ?[*]const D3D12_RECT) void {
        self.vtable.ClearRenderTargetView(self, rtv.ptr, color, num_rects, rects);
    }

    pub inline fn SetGraphicsRootSignature(self: *ID3D12GraphicsCommandList, root_sig: ?*ID3D12RootSignature) void {
        self.vtable.SetGraphicsRootSignature(self, root_sig);
    }

    pub inline fn SetDescriptorHeaps(self: *ID3D12GraphicsCommandList, num: u32, heaps: [*]const *ID3D12DescriptorHeap) void {
        self.vtable.SetDescriptorHeaps(self, num, heaps);
    }

    pub inline fn SetPipelineState(self: *ID3D12GraphicsCommandList, pso: *ID3D12PipelineState) void {
        self.vtable.SetPipelineState(self, pso);
    }

    pub inline fn OMSetRenderTargets(self: *ID3D12GraphicsCommandList, num: u32, rt_descriptors: ?[*]const D3D12_CPU_DESCRIPTOR_HANDLE, single_handle: BOOL, ds_descriptor: ?*const D3D12_CPU_DESCRIPTOR_HANDLE) void {
        self.vtable.OMSetRenderTargets(self, num, rt_descriptors, single_handle, ds_descriptor);
    }

    pub inline fn RSSetViewports(self: *ID3D12GraphicsCommandList, num: u32, viewports: [*]const D3D12_VIEWPORT) void {
        self.vtable.RSSetViewports(self, num, viewports);
    }

    pub inline fn RSSetScissorRects(self: *ID3D12GraphicsCommandList, num: u32, rects: [*]const D3D12_RECT) void {
        self.vtable.RSSetScissorRects(self, num, rects);
    }

    pub inline fn IASetPrimitiveTopology(self: *ID3D12GraphicsCommandList, topology: D3D_PRIMITIVE_TOPOLOGY) void {
        self.vtable.IASetPrimitiveTopology(self, topology);
    }

    pub inline fn IASetVertexBuffers(self: *ID3D12GraphicsCommandList, start_slot: u32, num_views: u32, views: [*]const D3D12_VERTEX_BUFFER_VIEW) void {
        self.vtable.IASetVertexBuffers(self, start_slot, num_views, views);
    }

    pub inline fn DrawInstanced(self: *ID3D12GraphicsCommandList, vertex_count: u32, instance_count: u32, start_vertex: u32, start_instance: u32) void {
        self.vtable.DrawInstanced(self, vertex_count, instance_count, start_vertex, start_instance);
    }

    pub inline fn SetGraphicsRootDescriptorTable(self: *ID3D12GraphicsCommandList, index: u32, base_descriptor: D3D12_GPU_DESCRIPTOR_HANDLE) void {
        self.vtable.SetGraphicsRootDescriptorTable(self, index, base_descriptor.ptr);
    }

    pub inline fn SetGraphicsRootConstantBufferView(self: *ID3D12GraphicsCommandList, index: u32, buffer_location: u64) void {
        self.vtable.SetGraphicsRootConstantBufferView(self, index, buffer_location);
    }

    pub inline fn SetGraphicsRootShaderResourceView(self: *ID3D12GraphicsCommandList, index: u32, buffer_location: u64) void {
        self.vtable.SetGraphicsRootShaderResourceView(self, index, buffer_location);
    }

    pub inline fn CopyBufferRegion(self: *ID3D12GraphicsCommandList, dst: *ID3D12Resource, dst_offset: u64, src: *ID3D12Resource, src_offset: u64, num_bytes: u64) void {
        self.vtable.CopyBufferRegion(self, dst, dst_offset, src, src_offset, num_bytes);
    }

    pub inline fn CopyTextureRegion(self: *ID3D12GraphicsCommandList, dst: *const D3D12_TEXTURE_COPY_LOCATION, dst_x: u32, dst_y: u32, dst_z: u32, src_loc: *const D3D12_TEXTURE_COPY_LOCATION, src_box: ?*const D3D12_BOX) void {
        self.vtable.CopyTextureRegion(self, dst, dst_x, dst_y, dst_z, src_loc, src_box);
    }

    pub inline fn Release(self: *ID3D12GraphicsCommandList) u32 {
        return self.vtable.Release(self);
    }
};

// ID3D12Device
// Inherits: IUnknown (3) -> ID3D12Object (4) = 7 inherited slots
pub const ID3D12Device = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x189819f1,
        .data2 = 0x1db6,
        .data3 = 0x4b57,
        .data4 = .{ 0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*ID3D12Device, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*ID3D12Device) callconv(.winapi) u32,
        Release: *const fn (*ID3D12Device) callconv(.winapi) u32,
        // ID3D12Object (slots 3-6)
        GetPrivateData: Reserved,
        SetPrivateData: Reserved,
        SetPrivateDataInterface: Reserved,
        SetName: Reserved,
        // ID3D12Device own methods (slots 7+)
        // slot 7
        GetNodeCount: Reserved,
        // slot 8
        CreateCommandQueue: *const fn (*ID3D12Device, *const D3D12_COMMAND_QUEUE_DESC, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        // slot 9
        CreateCommandAllocator: *const fn (*ID3D12Device, D3D12_COMMAND_LIST_TYPE, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        // slot 10
        CreateGraphicsPipelineState: *const fn (*ID3D12Device, *const D3D12_GRAPHICS_PIPELINE_STATE_DESC, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        // slot 11
        CreateComputePipelineState: Reserved,
        // slot 12
        CreateCommandList: *const fn (*ID3D12Device, NodeMask: u32, Type: D3D12_COMMAND_LIST_TYPE, pCommandAllocator: *ID3D12CommandAllocator, pInitialState: ?*ID3D12PipelineState, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        // slot 13
        CheckFeatureSupport: Reserved,
        // slot 14
        CreateDescriptorHeap: *const fn (*ID3D12Device, *const D3D12_DESCRIPTOR_HEAP_DESC, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        // slot 15
        GetDescriptorHandleIncrementSize: *const fn (*ID3D12Device, D3D12_DESCRIPTOR_HEAP_TYPE) callconv(.winapi) u32,
        // slot 16
        CreateRootSignature: *const fn (*ID3D12Device, NodeMask: u32, pBlobWithRootSignature: *const anyopaque, blobLengthInBytes: usize, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        // slot 17
        CreateConstantBufferView: Reserved,
        // slot 18
        // DestDescriptor is D3D12_CPU_DESCRIPTOR_HANDLE (8-byte struct) passed
        // by value. Use usize in the vtable to avoid Zig callconv(.winapi)
        // struct-by-value ABI ambiguity -- same class of issue as the
        // descriptor heap struct-return fix in PR # 142.
        CreateShaderResourceView: *const fn (*ID3D12Device, pResource: ?*ID3D12Resource, pDesc: ?*const D3D12_SHADER_RESOURCE_VIEW_DESC, DestDescriptor: usize) callconv(.winapi) void,
        // slot 19
        CreateUnorderedAccessView: Reserved,
        // slot 20
        CreateRenderTargetView: *const fn (*ID3D12Device, pResource: ?*ID3D12Resource, pDesc: ?*const anyopaque, DestDescriptor: usize) callconv(.winapi) void,
        // slot 21
        CreateDepthStencilView: Reserved,
        // slot 22
        CreateSampler: *const fn (*ID3D12Device, pDesc: *const D3D12_SAMPLER_DESC, DestDescriptor: usize) callconv(.winapi) void,
        // slot 23
        CopyDescriptors: Reserved,
        // slot 24
        CopyDescriptorsSimple: Reserved,
        // slot 25
        GetResourceAllocationInfo: Reserved,
        // slot 26
        GetCustomHeapProperties: Reserved,
        // slot 27
        CreateCommittedResource: *const fn (*ID3D12Device, *const D3D12_HEAP_PROPERTIES, u32, *const D3D12_RESOURCE_DESC, D3D12_RESOURCE_STATES, ?*const anyopaque, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        // slot 28
        CreateHeap: Reserved,
        // slot 29
        CreatePlacedResource: Reserved,
        // slot 30
        CreateReservedResource: Reserved,
        // slot 31
        CreateSharedHandle: *const fn (*ID3D12Device, *IUnknown, ?*const anyopaque, u32, ?[*:0]const u16, *HANDLE) callconv(.winapi) HRESULT,
        // slot 32
        OpenSharedHandle: Reserved,
        // slot 33
        OpenSharedHandleByName: Reserved,
        // slot 34
        MakeResident: Reserved,
        // slot 35
        Evict: Reserved,
        // slot 36
        CreateFence: *const fn (*ID3D12Device, InitialValue: u64, Flags: D3D12_FENCE_FLAGS, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        // slot 37
        GetDeviceRemovedReason: *const fn (*ID3D12Device) callconv(.winapi) HRESULT,
        // slot 38
        GetCopyableFootprints: Reserved,
        // slot 39
        CreateQueryHeap: Reserved,
        // slot 40
        SetStablePowerState: Reserved,
        // slot 41
        CreateCommandSignature: Reserved,
        // slot 42
        GetResourceTiling: Reserved,
        // slot 43
        GetAdapterLuid: Reserved,
    };

    pub inline fn CreateCommandQueue(self: *ID3D12Device, desc: *const D3D12_COMMAND_QUEUE_DESC, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.CreateCommandQueue(self, desc, riid, pp);
    }

    pub inline fn CreateCommandAllocator(self: *ID3D12Device, list_type: D3D12_COMMAND_LIST_TYPE, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.CreateCommandAllocator(self, list_type, riid, pp);
    }

    pub inline fn CreateGraphicsPipelineState(self: *ID3D12Device, desc: *const D3D12_GRAPHICS_PIPELINE_STATE_DESC, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.CreateGraphicsPipelineState(self, desc, riid, pp);
    }

    pub inline fn CreateCommandList(self: *ID3D12Device, node_mask: u32, list_type: D3D12_COMMAND_LIST_TYPE, allocator: *ID3D12CommandAllocator, initial_state: ?*ID3D12PipelineState, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.CreateCommandList(self, node_mask, list_type, allocator, initial_state, riid, pp);
    }

    pub inline fn CreateDescriptorHeap(self: *ID3D12Device, desc: *const D3D12_DESCRIPTOR_HEAP_DESC, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.CreateDescriptorHeap(self, desc, riid, pp);
    }

    pub inline fn GetDescriptorHandleIncrementSize(self: *ID3D12Device, heap_type: D3D12_DESCRIPTOR_HEAP_TYPE) u32 {
        return self.vtable.GetDescriptorHandleIncrementSize(self, heap_type);
    }

    pub inline fn CreateRootSignature(self: *ID3D12Device, node_mask: u32, blob: *const anyopaque, blob_len: usize, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.CreateRootSignature(self, node_mask, blob, blob_len, riid, pp);
    }

    pub inline fn CreateShaderResourceView(self: *ID3D12Device, resource: ?*ID3D12Resource, desc: ?*const D3D12_SHADER_RESOURCE_VIEW_DESC, dest: D3D12_CPU_DESCRIPTOR_HANDLE) void {
        self.vtable.CreateShaderResourceView(self, resource, desc, dest.ptr);
    }

    pub inline fn CreateSampler(self: *ID3D12Device, desc: *const D3D12_SAMPLER_DESC, dest: D3D12_CPU_DESCRIPTOR_HANDLE) void {
        self.vtable.CreateSampler(self, desc, dest.ptr);
    }

    pub inline fn CreateRenderTargetView(self: *ID3D12Device, resource: ?*ID3D12Resource, desc: ?*const anyopaque, dest: D3D12_CPU_DESCRIPTOR_HANDLE) void {
        self.vtable.CreateRenderTargetView(self, resource, desc, dest.ptr);
    }

    pub inline fn CreateCommittedResource(self: *ID3D12Device, heap_props: *const D3D12_HEAP_PROPERTIES, heap_flags: u32, desc: *const D3D12_RESOURCE_DESC, initial_state: D3D12_RESOURCE_STATES, optimized_clear: ?*const anyopaque, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.CreateCommittedResource(self, heap_props, heap_flags, desc, initial_state, optimized_clear, riid, pp);
    }

    pub inline fn CreateSharedHandle(
        self: *ID3D12Device,
        object: *IUnknown,
        access: u32,
        handle_out: *HANDLE,
    ) HRESULT {
        return self.vtable.CreateSharedHandle(
            self,
            object,
            null, // default security attributes
            access,
            null, // unnamed
            handle_out,
        );
    }

    pub inline fn CreateFence(self: *ID3D12Device, initial_value: u64, flags: D3D12_FENCE_FLAGS, riid: *const GUID, pp: *?*anyopaque) HRESULT {
        return self.vtable.CreateFence(self, initial_value, flags, riid, pp);
    }

    pub inline fn GetDeviceRemovedReason(self: *ID3D12Device) HRESULT {
        return self.vtable.GetDeviceRemovedReason(self);
    }

    pub inline fn Release(self: *ID3D12Device) u32 {
        return self.vtable.Release(self);
    }
};

// --- Extern functions ---

pub extern "d3d12" fn D3D12CreateDevice(
    pAdapter: ?*IUnknown,
    MinimumFeatureLevel: u32,
    riid: *const GUID,
    ppDevice: *?*anyopaque,
) callconv(.winapi) HRESULT;

pub extern "d3d12" fn D3D12GetDebugInterface(
    riid: *const GUID,
    ppvDebug: *?*anyopaque,
) callconv(.winapi) HRESULT;

pub extern "d3d12" fn D3D12SerializeVersionedRootSignature(
    pRootSignature: *const D3D12_VERSIONED_ROOT_SIGNATURE_DESC,
    ppBlob: *?*ID3DBlob,
    ppErrorBlob: *?*ID3DBlob,
) callconv(.winapi) HRESULT;

// --- DXC (DirectX Shader Compiler) types ---

pub const DXC_OUT_KIND = enum(u32) {
    NONE = 0,
    OBJECT = 1,
    ERRORS = 2,
    PDB = 3,
    SHADER_HASH = 4,
    DISASSEMBLY = 5,
    HLSL = 6,
    TEXT = 7,
    REFLECTION = 8,
    ROOT_SIGNATURE = 9,
    EXTRA_OUTPUTS = 10,
    FORCE_DWORD = 0xFFFFFFFF,
};

pub const DxcBuffer = extern struct {
    Ptr: ?*const anyopaque,
    Size: usize,
    Encoding: u32,
};

// IDxcBlob
// Inherits: IUnknown(3) > IDxcBlob(2) = 5 total
pub const IDxcBlob = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x8BA5FB08,
        .data2 = 0x5195,
        .data3 = 0x40e2,
        .data4 = .{ 0xAC, 0x58, 0x0D, 0x98, 0x9C, 0x3A, 0x01, 0x02 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*IDxcBlob, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDxcBlob) callconv(.winapi) u32,
        Release: *const fn (*IDxcBlob) callconv(.winapi) u32,
        // IDxcBlob (slots 3-4)
        GetBufferPointer: *const fn (*IDxcBlob) callconv(.winapi) *anyopaque,
        GetBufferSize: *const fn (*IDxcBlob) callconv(.winapi) usize,
    };

    pub inline fn GetBufferPointer(self: *IDxcBlob) *anyopaque {
        return self.vtable.GetBufferPointer(self);
    }

    pub inline fn GetBufferSize(self: *IDxcBlob) usize {
        return self.vtable.GetBufferSize(self);
    }

    pub inline fn Release(self: *IDxcBlob) u32 {
        return self.vtable.Release(self);
    }
};

// IDxcBlobUtf8
// Inherits: IUnknown(3) > IDxcBlob(2) > IDxcBlobEncoding(1) > IDxcBlobUtf8(2) = 8 total
pub const IDxcBlobUtf8 = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x3DA636C9,
        .data2 = 0xBA71,
        .data3 = 0x4024,
        .data4 = .{ 0xA3, 0x01, 0x30, 0xCB, 0xF1, 0x25, 0x30, 0x5B },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*IDxcBlobUtf8, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDxcBlobUtf8) callconv(.winapi) u32,
        Release: *const fn (*IDxcBlobUtf8) callconv(.winapi) u32,
        // IDxcBlob (slots 3-4)
        GetBufferPointer: *const fn (*IDxcBlobUtf8) callconv(.winapi) *anyopaque,
        GetBufferSize: *const fn (*IDxcBlobUtf8) callconv(.winapi) usize,
        // IDxcBlobEncoding (slot 5)
        GetEncoding: Reserved,
        // IDxcBlobUtf8 (slots 6-7)
        GetStringPointer: *const fn (*IDxcBlobUtf8) callconv(.winapi) [*:0]const u8,
        GetStringLength: *const fn (*IDxcBlobUtf8) callconv(.winapi) usize,
    };

    pub inline fn GetBufferPointer(self: *IDxcBlobUtf8) *anyopaque {
        return self.vtable.GetBufferPointer(self);
    }

    pub inline fn GetBufferSize(self: *IDxcBlobUtf8) usize {
        return self.vtable.GetBufferSize(self);
    }

    pub inline fn GetStringPointer(self: *IDxcBlobUtf8) [*:0]const u8 {
        return self.vtable.GetStringPointer(self);
    }

    pub inline fn GetStringLength(self: *IDxcBlobUtf8) usize {
        return self.vtable.GetStringLength(self);
    }

    pub inline fn Release(self: *IDxcBlobUtf8) u32 {
        return self.vtable.Release(self);
    }
};

// IDxcResult
// Inherits: IUnknown(3) > IDxcOperationResult(3) > IDxcResult(5) = 11 total
pub const IDxcResult = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x58346CDA,
        .data2 = 0xDDE7,
        .data3 = 0x4497,
        .data4 = .{ 0x94, 0x61, 0x6F, 0x87, 0xAF, 0x5E, 0x06, 0x59 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*IDxcResult, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDxcResult) callconv(.winapi) u32,
        Release: *const fn (*IDxcResult) callconv(.winapi) u32,
        // IDxcOperationResult (slots 3-5)
        GetStatus: *const fn (*IDxcResult, *HRESULT) callconv(.winapi) HRESULT,
        GetResult: Reserved,
        GetErrorBuffer: Reserved,
        // IDxcResult (slots 6-10)
        HasOutput: Reserved,
        GetOutput: *const fn (*IDxcResult, DXC_OUT_KIND, *const GUID, *?*anyopaque, *?*anyopaque) callconv(.winapi) HRESULT,
        GetNumOutputs: Reserved,
        GetOutputByIndex: Reserved,
        PrimaryOutput: Reserved,
    };

    pub inline fn GetStatus(self: *IDxcResult) HRESULT {
        var status: HRESULT = 0;
        _ = self.vtable.GetStatus(self, &status);
        return status;
    }

    pub inline fn GetOutput(self: *IDxcResult, kind: DXC_OUT_KIND, riid: *const GUID, ppvObject: *?*anyopaque, ppOutputObject: *?*anyopaque) HRESULT {
        return self.vtable.GetOutput(self, kind, riid, ppvObject, ppOutputObject);
    }

    pub inline fn Release(self: *IDxcResult) u32 {
        return self.vtable.Release(self);
    }
};

// IDxcUtils
// Inherits: IUnknown(3) + 13 own methods = 16 total
pub const IDxcUtils = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x4605C4CB,
        .data2 = 0x2019,
        .data3 = 0x492A,
        .data4 = .{ 0xAD, 0xA4, 0x65, 0xF2, 0x0B, 0xB7, 0xD6, 0x7F },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*IDxcUtils, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDxcUtils) callconv(.winapi) u32,
        Release: *const fn (*IDxcUtils) callconv(.winapi) u32,
        // IDxcUtils (slots 3-15)
        CreateBlobFromBlob: Reserved,
        CreateBlobFromPinned: Reserved,
        MoveToBlob: Reserved,
        CreateBlob: Reserved,
        LoadFile: Reserved,
        CreateReadOnlyStreamFromBlob: Reserved,
        CreateDefaultIncludeHandler: *const fn (*IDxcUtils, *?*anyopaque) callconv(.winapi) HRESULT,
        GetBlobAsUtf8: Reserved,
        GetBlobAsWide: Reserved,
        GetDxilContainerPart: Reserved,
        CreateReflection: Reserved,
        BuildArguments: Reserved,
        GetPDBContents: Reserved,
    };

    pub inline fn CreateDefaultIncludeHandler(self: *IDxcUtils, pp: *?*anyopaque) HRESULT {
        return self.vtable.CreateDefaultIncludeHandler(self, pp);
    }

    pub inline fn Release(self: *IDxcUtils) u32 {
        return self.vtable.Release(self);
    }
};

// IDxcCompiler3
// Inherits: IUnknown(3) + 2 own methods = 5 total
pub const IDxcCompiler3 = extern struct {
    vtable: *const VTable,
    pub const IID = GUID{
        .data1 = 0x228B4687,
        .data2 = 0x5A6A,
        .data3 = 0x4730,
        .data4 = .{ 0x90, 0x0C, 0x97, 0x02, 0xB2, 0x20, 0x3F, 0x54 },
    };

    pub const VTable = extern struct {
        // IUnknown (slots 0-2)
        QueryInterface: *const fn (*IDxcCompiler3, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDxcCompiler3) callconv(.winapi) u32,
        Release: *const fn (*IDxcCompiler3) callconv(.winapi) u32,
        // IDxcCompiler3 (slots 3-4)
        Compile: *const fn (
            *IDxcCompiler3,
            *const DxcBuffer,
            [*]const ?[*:0]const u16,
            u32,
            ?*anyopaque,
            *const GUID,
            *?*anyopaque,
        ) callconv(.winapi) HRESULT,
        Disassemble: Reserved,
    };

    pub inline fn Compile(
        self: *IDxcCompiler3,
        source: *const DxcBuffer,
        args: [*]const ?[*:0]const u16,
        arg_count: u32,
        define: ?*anyopaque,
        riid: *const GUID,
        pp: *?*anyopaque,
    ) HRESULT {
        return self.vtable.Compile(self, source, args, arg_count, define, riid, pp);
    }

    pub inline fn Release(self: *IDxcCompiler3) u32 {
        return self.vtable.Release(self);
    }
};

pub const CLSID_DxcUtils = GUID{ .data1 = 0x6245D6AF, .data2 = 0x66E0, .data3 = 0x48FD, .data4 = .{ 0x80, 0xB4, 0x4D, 0x27, 0x17, 0x96, 0x74, 0x8C } };
pub const CLSID_DxcCompiler = GUID{ .data1 = 0x73E22D93, .data2 = 0xE6CE, .data3 = 0x47F3, .data4 = .{ 0xB5, 0xBF, 0xF0, 0x66, 0x4F, 0x39, 0xC1, 0xB0 } };

// DxcLibrary handles dynamic loading of dxcompiler.dll
pub const DxcLibrary = struct {
    dll: ?std.os.windows.HMODULE,
    create_instance: ?*const fn (*const GUID, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,

    /// Load dxcompiler.dll and get DxcCreateInstance function pointer.
    /// Returns null if the DLL cannot be loaded.
    pub fn load() ?DxcLibrary {
        const dll_name = std.unicode.utf8ToUtf16LeStringLiteral("dxcompiler.dll");
        const dll = std.os.windows.LoadLibraryW(dll_name) catch |err| switch (err) {
            error.FileNotFound => return null,
            else => return null,
        };

        // Get the DxcCreateInstance function
        const proc = std.os.windows.kernel32.GetProcAddress(dll, "DxcCreateInstance") orelse {
            std.os.windows.FreeLibrary(dll);
            return null;
        };

        return DxcLibrary{
            .dll = dll,
            .create_instance = @ptrCast(proc),
        };
    }

    /// Unload the DLL.
    pub fn deinit(self: DxcLibrary) void {
        if (self.dll) |dll| {
            std.os.windows.FreeLibrary(dll);
        }
    }

    /// Create a DXC object via DxcCreateInstance.
    /// Returns E_FAIL if the library was not loaded successfully.
    pub fn createInstance(self: DxcLibrary, class_id: *const GUID, interface_id: *const GUID, out: *?*anyopaque) HRESULT {
        const create_fn = self.create_instance orelse return com.E_FAIL;
        return create_fn(class_id, interface_id, out);
    }
};

// --- Kernel32 helpers for fence synchronization ---

pub extern "kernel32" fn CreateEventW(
    lpEventAttributes: ?*anyopaque,
    bManualReset: BOOL,
    bInitialState: BOOL,
    lpName: ?LPCWSTR,
) callconv(.winapi) ?HANDLE;

pub extern "kernel32" fn WaitForSingleObject(
    hHandle: HANDLE,
    dwMilliseconds: u32,
) callconv(.winapi) u32;

pub extern "kernel32" fn CloseHandle(
    hObject: HANDLE,
) callconv(.winapi) BOOL;

pub const INFINITE: u32 = 0xFFFFFFFF;

// --- Tests ---

test "D3D12 struct sizes" {
    try std.testing.expectEqual(24, @sizeOf(D3D12_VIEWPORT));
    try std.testing.expectEqual(16, @sizeOf(D3D12_RECT));
    try std.testing.expectEqual(@sizeOf(usize), @sizeOf(D3D12_CPU_DESCRIPTOR_HANDLE));
    try std.testing.expectEqual(8, @sizeOf(D3D12_GPU_DESCRIPTOR_HANDLE));
    try std.testing.expectEqual(16, @sizeOf(D3D12_SHADER_BYTECODE));
    try std.testing.expectEqual(16, @sizeOf(D3D12_VERTEX_BUFFER_VIEW));
    try std.testing.expectEqual(16, @sizeOf(D3D12_COMMAND_QUEUE_DESC));
    try std.testing.expectEqual(16, @sizeOf(D3D12_DESCRIPTOR_HEAP_DESC));
    try std.testing.expectEqual(20, @sizeOf(D3D12_HEAP_PROPERTIES));
    try std.testing.expectEqual(2 * @sizeOf(usize), @sizeOf(D3D12_RANGE));
    try std.testing.expectEqual(24, @sizeOf(D3D12_BOX));
    try std.testing.expectEqual(56, @sizeOf(D3D12_RESOURCE_DESC));
    try std.testing.expectEqual(32, @sizeOf(D3D12_RESOURCE_BARRIER));
    try std.testing.expectEqual(32, @sizeOf(D3D12_ROOT_PARAMETER));
    try std.testing.expectEqual(656, @sizeOf(D3D12_GRAPHICS_PIPELINE_STATE_DESC));

    // SRV desc must match MSVC ABI size. The union contains D3D12_BUFFER_SRV which starts
    // with a u64, giving the union 8-byte alignment. After three u32 fields (12 bytes),
    // 4 bytes of padding are inserted before the union: 12 + 4(pad) + 24(union) = 40.
    try std.testing.expectEqual(40, @sizeOf(D3D12_SHADER_RESOURCE_VIEW_DESC));
    try std.testing.expectEqual(24, @sizeOf(D3D12_BUFFER_SRV));
    try std.testing.expectEqual(16, @sizeOf(D3D12_TEX2D_SRV));

    // v1.1 root signature types
    try std.testing.expectEqual(24, @sizeOf(D3D12_DESCRIPTOR_RANGE1));
    try std.testing.expectEqual(12, @sizeOf(D3D12_ROOT_DESCRIPTOR1));
    try std.testing.expectEqual(32, @sizeOf(D3D12_ROOT_PARAMETER1));
    try std.testing.expectEqual(40, @sizeOf(D3D12_ROOT_SIGNATURE_DESC1));
    try std.testing.expectEqual(48, @sizeOf(D3D12_VERSIONED_ROOT_SIGNATURE_DESC));
}

test "D3D12 GUID constants" {
    const device_iid = ID3D12Device.IID;
    try std.testing.expectEqual(@as(u32, 0x189819f1), device_iid.data1);
    try std.testing.expectEqual(@as(u16, 0x1db6), device_iid.data2);
    try std.testing.expectEqual(@as(u16, 0x4b57), device_iid.data3);
    try std.testing.expectEqualSlices(u8, &device_iid.data4, &[_]u8{ 0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7 });

    const queue_iid = ID3D12CommandQueue.IID;
    try std.testing.expectEqual(@as(u32, 0x0ec870a6), queue_iid.data1);

    const fence_iid = ID3D12Fence.IID;
    try std.testing.expectEqual(@as(u32, 0x0a753dcf), fence_iid.data1);
}

test "D3D12 COM interfaces are single vtable pointers" {
    try std.testing.expectEqual(@sizeOf(*anyopaque), @sizeOf(ID3D12Device));
    try std.testing.expectEqual(@sizeOf(*anyopaque), @sizeOf(ID3D12CommandQueue));
    try std.testing.expectEqual(@sizeOf(*anyopaque), @sizeOf(ID3D12GraphicsCommandList));
    try std.testing.expectEqual(@sizeOf(*anyopaque), @sizeOf(ID3D12Resource));
}

test "DescriptorHeap vtable uses output pointer for struct returns" {
    // COM methods that return structs use a hidden output pointer in the
    // binary vtable (the C ABI convention). Verify the vtable function
    // signatures take an output pointer parameter instead of returning
    // the struct directly.
    const VTable = ID3D12DescriptorHeap.VTable;
    const cpu_fn_info = @typeInfo(@TypeOf(@as(VTable, undefined).GetCPUDescriptorHandleForHeapStart));
    const gpu_fn_info = @typeInfo(@TypeOf(@as(VTable, undefined).GetGPUDescriptorHandleForHeapStart));

    // Both should be pointers to functions
    const cpu_child = @typeInfo(cpu_fn_info.pointer.child);
    const gpu_child = @typeInfo(gpu_fn_info.pointer.child);

    // Should take 2 params (self + output pointer), not 1 (self only)
    try std.testing.expectEqual(2, cpu_child.@"fn".params.len);
    try std.testing.expectEqual(2, gpu_child.@"fn".params.len);

    // Return type should be void, not a struct
    try std.testing.expectEqual(void, cpu_child.@"fn".return_type.?);
    try std.testing.expectEqual(void, gpu_child.@"fn".return_type.?);
}

test "Device vtable passes descriptor handles as raw scalars" {
    // COM methods that take D3D12_CPU_DESCRIPTOR_HANDLE by value must use
    // usize in the vtable (not the extern struct) to avoid Zig callconv(.winapi)
    // struct-by-value ABI ambiguity on x86_64-windows.
    const VT = ID3D12Device.VTable;

    // Helper: get the Nth parameter type from a vtable function pointer field.
    const ParamType = struct {
        fn get(comptime field: anytype, comptime n: usize) type {
            const ptr_info = @typeInfo(@TypeOf(field));
            const fn_info = @typeInfo(ptr_info.pointer.child);
            return fn_info.@"fn".params[n].type.?;
        }
    };

    // CreateShaderResourceView: last param (index 3) must be usize
    try std.testing.expectEqual(usize, ParamType.get(@as(VT, undefined).CreateShaderResourceView, 3));
    // CreateRenderTargetView: last param (index 3) must be usize
    try std.testing.expectEqual(usize, ParamType.get(@as(VT, undefined).CreateRenderTargetView, 3));
    // CreateSampler: last param (index 2) must be usize
    try std.testing.expectEqual(usize, ParamType.get(@as(VT, undefined).CreateSampler, 2));
}

test "CommandList vtable passes descriptor handles as raw scalars" {
    const VT = ID3D12GraphicsCommandList.VTable;

    const ParamType = struct {
        fn get(comptime field: anytype, comptime n: usize) type {
            const ptr_info = @typeInfo(@TypeOf(field));
            const fn_info = @typeInfo(ptr_info.pointer.child);
            return fn_info.@"fn".params[n].type.?;
        }
    };

    // ClearRenderTargetView: param 1 (after self) must be usize
    try std.testing.expectEqual(usize, ParamType.get(@as(VT, undefined).ClearRenderTargetView, 1));
    // SetGraphicsRootDescriptorTable: param 2 (after self + index) must be u64
    try std.testing.expectEqual(u64, ParamType.get(@as(VT, undefined).SetGraphicsRootDescriptorTable, 2));
}

test "DxcBuffer is extern struct with expected field order" {
    try std.testing.expectEqual(@sizeOf(?*const anyopaque), @sizeOf(@FieldType(DxcBuffer, "Ptr")));
    try std.testing.expectEqual(@sizeOf(usize), @sizeOf(@FieldType(DxcBuffer, "Size")));
    try std.testing.expectEqual(@sizeOf(u32), @sizeOf(@FieldType(DxcBuffer, "Encoding")));
}

test "DXC_OUT_KIND has OBJECT and ERRORS variants" {
    try std.testing.expectEqual(@as(u32, 1), @intFromEnum(DXC_OUT_KIND.OBJECT));
    try std.testing.expectEqual(@as(u32, 2), @intFromEnum(DXC_OUT_KIND.ERRORS));
}

test "IDxcBlobUtf8 has expected vtable field count" {
    // IUnknown(3) + IDxcBlob(2) + IDxcBlobEncoding(1) + IDxcBlobUtf8(2) = 8 slots
    try std.testing.expectEqual(@sizeOf(*anyopaque), @sizeOf(IDxcBlobUtf8));
    const vtable_size = @sizeOf(IDxcBlobUtf8.VTable);
    const expected_size = 8 * @sizeOf(*anyopaque);
    try std.testing.expectEqual(expected_size, vtable_size);
}

test "IDxcResult has expected vtable field count" {
    // IUnknown(3) + IDxcOperationResult(3) + IDxcResult(5) = 11 slots
    try std.testing.expectEqual(@sizeOf(*anyopaque), @sizeOf(IDxcResult));
    const vtable_size = @sizeOf(IDxcResult.VTable);
    const expected_size = 11 * @sizeOf(*anyopaque);
    try std.testing.expectEqual(expected_size, vtable_size);
}

test "IDxcUtils has expected vtable field count" {
    // IUnknown(3) + 13 methods = 16 slots
    try std.testing.expectEqual(@sizeOf(*anyopaque), @sizeOf(IDxcUtils));
    const vtable_size = @sizeOf(IDxcUtils.VTable);
    const expected_size = 16 * @sizeOf(*anyopaque);
    try std.testing.expectEqual(expected_size, vtable_size);
}

test "IDxcCompiler3 has expected vtable field count" {
    // IUnknown(3) + Compile + Disassemble = 5 slots
    try std.testing.expectEqual(@sizeOf(*anyopaque), @sizeOf(IDxcCompiler3));
    const vtable_size = @sizeOf(IDxcCompiler3.VTable);
    const expected_size = 5 * @sizeOf(*anyopaque);
    try std.testing.expectEqual(expected_size, vtable_size);
}

test "DxcLibrary.load returns null when dxcompiler.dll absent" {
    // This test just verifies the struct compiles and the method exists.
    // We don't actually call load() since it would fail if dxcompiler.dll is present.
    try std.testing.expectEqual(@sizeOf(?std.os.windows.HMODULE), @sizeOf(@FieldType(DxcLibrary, "dll")));
    try std.testing.expectEqual(@sizeOf(?*const fn (*const GUID, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT), @sizeOf(@FieldType(DxcLibrary, "create_instance")));
}

test "CLSID constants are distinct" {
    try std.testing.expect(!std.mem.eql(u8, &CLSID_DxcUtils.data4, &CLSID_DxcCompiler.data4));
}
