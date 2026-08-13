//! Renderer implementation and utilities. The renderer is responsible for
//! taking the internal screen state and turning into some output format,
//! usually for a screen.
//!
//! The renderer is closely tied to the windowing system which usually
//! has to prepare the window for the given renderer using system-specific
//! APIs. The renderers in this package assume that the renderer is already
//! setup (OpenGL has a context, Vulkan has a surface, etc.)

const build_config = @import("build_config.zig");

const cursor = @import("renderer/cursor.zig");
const message = @import("renderer/message.zig");
const size = @import("renderer/size.zig");
pub const shadertoy = @import("renderer/shadertoy.zig");
pub const Backend = @import("renderer/backend.zig").Backend;
pub const GenericRenderer = @import("renderer/generic.zig").Renderer;
pub const Metal = @import("renderer/Metal.zig");
pub const OpenGL = @import("renderer/OpenGL.zig");
pub const WebGL = @import("renderer/WebGL.zig");
pub const DirectX12 = if (build_config.renderer == .directx12) @import("renderer/DirectX12.zig") else struct {};
pub const Options = @import("renderer/Options.zig");
pub const Overlay = @import("renderer/Overlay.zig");
pub const Thread = @import("renderer/Thread.zig");
pub const State = @import("renderer/State.zig");
pub const CursorStyle = cursor.Style;
pub const Message = message.Message;
pub const Size = size.Size;
pub const Coordinate = size.Coordinate;
pub const CellSize = size.CellSize;
pub const ScreenSize = size.ScreenSize;
pub const GridSize = size.GridSize;
pub const Padding = size.Padding;
pub const cursorStyle = cursor.style;
pub const lib = @import("lib/main.zig");

/// The implementation to use for the renderer. This is comptime chosen
/// so that every build has exactly one renderer implementation.
pub const Renderer = switch (build_config.renderer) {
    .metal => GenericRenderer(Metal),
    .opengl => GenericRenderer(OpenGL),
    .webgl => WebGL,
    .directx12 => GenericRenderer(DirectX12),
};

/// The health status of a renderer. These must be shared across all
/// renderers even if some states aren't reachable so that our API users
/// can use the same enum for all renderers.
pub const Health = enum(c_int) {
    healthy,
    unhealthy,

    test "ghostty.h Health" {
        try lib.checkGhosttyHEnum(Health, "GHOSTTY_RENDERER_HEALTH_");
    }
};

/// Why a configured `custom-shader` is not being applied. Like `Health`,
/// this is shared across all renderers even though not every variant is
/// reachable on every one, so API users get a single enum.
///
/// A failure here is not fatal: the renderer falls back to drawing straight
/// to the target, so the terminal looks entirely normal and the user has no
/// way to tell their shader silently did nothing. That is what this reason
/// exists to report.
pub const CustomShaderFailure = enum(c_int) {
    /// The shader file could not be read, or could not be translated into
    /// the backend's shading language.
    load_failed,

    /// The backend has no shader compiler available at runtime. On DX12
    /// this means dxcompiler.dll was not found next to the executable.
    compiler_unavailable,

    /// The compiler ran and rejected the shader source.
    compile_failed,

    /// The shader compiled but no GPU pipeline could be built from it.
    pipeline_failed,

    test "ghostty.h CustomShaderFailure" {
        try lib.checkGhosttyHEnum(
            CustomShaderFailure,
            "GHOSTTY_CUSTOM_SHADER_FAILURE_",
        );
    }
};

test {
    // Our comptime-chosen renderer
    _ = Renderer;

    _ = cursor;
    _ = message;
    _ = shadertoy;
    _ = size;
    _ = Thread;
    _ = State;
}
