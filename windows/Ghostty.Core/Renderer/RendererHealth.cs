namespace Ghostty.Core.Renderer;

/// <summary>
/// Whether a surface's renderer is drawing. Mirrors
/// <c>ghostty_action_renderer_health_e</c> in include/ghostty.h
/// (renderer.Health in src/renderer.zig).
/// </summary>
/// <remarks>
/// Unhealthy means the surface is not painting: on Windows that is a lost
/// GPU device (a TDR, a driver upgrade, an adapter pulled out) and the
/// renderer is rebuilding everything it owned. Healthy again means the
/// rebuild landed. A surface the renderer has given up on stays unhealthy.
///
/// The ordinals are ABI, not an implementation detail: they arrive as a raw
/// int in the action payload. GhosttyActionTagHeaderParityTests reads them
/// out of include/ghostty.h and checks both directions.
/// </remarks>
public enum RendererHealth
{
    Healthy = 0,
    Unhealthy = 1,
}
