using Ghostty.Core.Version;
using Ghostty.Motion;

namespace Ghostty.Services;

/// <summary>
/// The resolved animation state, for reporting. Answers with the ambient
/// floor the Appearance card gates: the same seat MotionGating's fallback
/// reads for effects outside the strips, with the persisted lever and the
/// OS animation preference both speaking. The vocabulary is the dump's:
/// "full", "reduced" or "off", one word each, so a pasted support dump
/// carries no internal identifiers.
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

    /// <summary>
    /// The version dialog's dump, composed fresh on every ask: the full
    /// clipboard payload (header + URL line + body, the bug-report use
    /// case) and the body alone the dialog displays, both over the state
    /// resolved at the moment of the ask. The copy handler asks per click,
    /// so a dump pasted after the resolved state changed -- the Animations
    /// lever moved in another window, battery saver engaged -- reports the
    /// state as it holds now, not as the dialog found it; and one ask
    /// feeds both the paste and the refreshed display, so the two cannot
    /// disagree.
    /// </summary>
    internal static (string Payload, string Body) ComposeDump()
    {
        var info = VersionRenderer.Build(Code());
        return (
            Payload: VersionRenderer.RenderPlain(info),
            Body: VersionRenderer.RenderPlainBody(info));
    }
}
