namespace Ghostty;

// Compile-time sentinel for the sponsor build gate.
//
// This constant is the ONLY sanctioned way for non-sponsor code to learn the
// flag state (for logs / diagnostics). Application logic must never branch on
// this: sponsor-gated behavior lives in windows/Ghostty/Sponsor/ and is absent
// from the output when SPONSOR_BUILD is not defined.
internal static class BuildFlags
{
#if SPONSOR_BUILD
    public const bool IsSponsorBuild = true;
#else
    public const bool IsSponsorBuild = false;
#endif
}
