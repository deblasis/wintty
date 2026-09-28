using Ghostty.Motion;

namespace Ghostty.Services;

/// <summary>
/// The resolved animation state, for reporting. Answers with the ambient
/// floor the Appearance card gates: the same seat MotionGating's fallback
/// reads for effects outside the strips, with the persisted lever and the
/// OS animation preference both speaking. The vocabulary is the dump's,
/// not the engine's: "full", "reduced" or "off", one word each, so a
/// pasted support dump carries no internal identifiers.
/// </summary>
internal static class AnimationsState
{
    public static string Code()
        => MotionGating.Effective(
               MotionSurfaceClass.Ambient,
               animationsEnabled: SystemAnimations.ReadAnimationsEnabled(),
               highContrast: false)
           switch
           {
               MotionPolicyLevel.Reduced => "reduced",
               MotionPolicyLevel.Off => "off",
               _ => "full",
           };
}
