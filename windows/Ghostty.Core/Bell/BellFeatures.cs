namespace Ghostty.Core.Bell;

/// <summary>
/// Decoded view of the core's <c>bell-features</c> packed struct.
/// <c>ghostty_config_get("bell-features", ...)</c> writes the struct's
/// bit representation into a <c>c_uint</c>; the field order is fixed by
/// the Zig declaration in <c>src/config/Config.zig</c>:
/// <c>system, audio, attention, title, border</c> (LSB first). Keeping
/// the decode here, behind one tested function, means the bit-position
/// contract is asserted in exactly one place.
/// </summary>
public readonly record struct BellFeatures(
    bool System,
    bool Audio,
    bool Attention,
    bool Title,
    bool Border)
{
    public static BellFeatures FromBits(uint bits) => new(
        System: (bits & (1u << 0)) != 0,
        Audio: (bits & (1u << 1)) != 0,
        Attention: (bits & (1u << 2)) != 0,
        Title: (bits & (1u << 3)) != 0,
        Border: (bits & (1u << 4)) != 0);

    /// <summary>True when no bell feature is enabled.</summary>
    public bool None => !System && !Audio && !Attention && !Title && !Border;
}
