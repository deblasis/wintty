using Ghostty.Core.Bell;

namespace Ghostty.Tests.Bell;

/// <summary>
/// Readable <see cref="BellFeatures"/> fixtures for bell tests, so the
/// gating tests pin to specific bits instead of a magic all-bits literal.
/// </summary>
internal static class BellFixtures
{
    public static readonly BellFeatures All = BellFeatures.FromBits(0x1F);
    public static readonly BellFeatures None = BellFeatures.FromBits(0);
    public static readonly BellFeatures AttentionOnly = BellFeatures.FromBits(1u << 2);
    public static readonly BellFeatures TitleOnly = BellFeatures.FromBits(1u << 3);
}
