namespace Ghostty.Core.Renderer;

/// <summary>
/// Why a configured <c>custom-shader</c> is not being applied. Mirrors
/// <c>ghostty_action_custom_shader_failure_e</c> in include/ghostty.h
/// (renderer.CustomShaderFailure in src/renderer.zig).
/// </summary>
/// <remarks>
/// The ordinals are ABI, not an implementation detail: they arrive as a raw
/// int in the action payload. GhosttyActionTagHeaderParityTests reads them
/// out of include/ghostty.h and checks both directions, so an upstream
/// reorder fails a test rather than silently showing the user the wrong
/// reason, and an upstream addition fails one rather than arriving as
/// whichever variant this list happens to end with.
/// </remarks>
public enum CustomShaderFailure
{
    LoadFailed = 0,
    CompilerUnavailable = 1,
    CompileFailed = 2,
    PipelineFailed = 3,
}
