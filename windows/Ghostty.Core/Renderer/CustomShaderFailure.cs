namespace Ghostty.Core.Renderer;

/// <summary>
/// Why a configured <c>custom-shader</c> is not being applied. Mirrors
/// <c>ghostty_action_custom_shader_failure_e</c> in include/ghostty.h
/// (renderer.CustomShaderFailure in src/renderer.zig).
/// </summary>
/// <remarks>
/// The ordinals are ABI, not an implementation detail — they arrive as a raw
/// int in the action payload. GhosttyActionsLayoutTests pins them against the
/// header so a reordering upstream fails a test rather than silently showing
/// the user the wrong reason.
/// </remarks>
public enum CustomShaderFailure
{
    LoadFailed = 0,
    CompilerUnavailable = 1,
    CompileFailed = 2,
    PipelineFailed = 3,
}
