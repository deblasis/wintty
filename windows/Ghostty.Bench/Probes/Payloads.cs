namespace Ghostty.Bench.Probes;

// Payload factories for throughput probes. Each returns exactly
// Payloads.TargetBytes (1 MB) of content.
//
// INVARIANT (enforced by PayloadsTests.Payloads_DoNotContainTerminatorPrefix):
// no payload may contain the literal substring "~ENDOFBURST_". The throughput
// probe appends "\r\n~ENDOFBURST_<nonce>~" after each payload and scans the
// return stream for that pattern; any in-payload occurrence of the prefix
// would risk a false match and cut the measurement short. The current three
// factories satisfy this trivially (ASCII 'A', SGR+short text, DCS/OSC/APC
// sequences interleaved with " printable " markers, none contain the
// prefix). New payloads must preserve the invariant.
//
// INVARIANT (implicit, not pinned by a test): payloads must end in a
// cleanly-parsed VT state. No unterminated DCS / OSC / APC / SOS / PM
// sequence may straddle the payload -> terminator boundary, or the parser
// (conhost) could consume the terminator's leading "\r\n" as part of the
// lingering state.
public static class Payloads
{
    // Sized to complete within the 180 s throughput watchdog. ConPTY's
    // emission pipeline for scrolling ASCII is bottlenecked at ~100 KB/s
    // (measured via conpty-throughput-verify): conhost emits one VT frame
    // per scrolled row (~87 bytes) at the refresh cadence, so a 1 MB
    // payload produces ~1.14 MB of hOutput traffic over ~8 s. Scaling to
    // the spec's aspirational 100 MB would cost ~20 minutes per iteration,
    // exceeding the harness budget. 1 MB keeps each probe iteration under
    // 10 s end-to-end while still producing a useful ConPTY vs DirectPipe
    // throughput comparison.
    public const int TargetBytes = 1 * 1024 * 1024;

    // 1 MB of uppercase 'A' (0x41). ConPTY's parser sees printables
    // only and takes the fast path; useful as a floor measurement.
    public static ReadOnlyMemory<byte> Ascii1Mb()
    {
        byte[] buf = new byte[TargetBytes];
        Array.Fill(buf, (byte)'A');
        return buf;
    }

    // 1 MB of "\x1b[31mhello\x1b[0m " repeated. Tests SGR parser
    // cost under realistic shell output.
    public static ReadOnlyMemory<byte> Sgr1Mb()
    {
        ReadOnlySpan<byte> unit = "\x1b[31mhello\x1b[0m "u8;
        byte[] buf = new byte[TargetBytes];
        int written = 0;
        while (written + unit.Length <= TargetBytes)
        {
            unit.CopyTo(buf.AsSpan(written));
            written += unit.Length;
        }
        // Pad remainder with 'A' so every byte is defined and the
        // total is exactly 1 MB.
        while (written < TargetBytes)
        {
            buf[written++] = (byte)'A';
        }
        return buf;
    }

    // 1 MB of dense DCS/OSC/APC sequences interleaved with short
    // printable runs. Pathological ConPTY parser load.
    public static ReadOnlyMemory<byte> Stress1Mb()
    {
        // One repeating chunk contains each introducer at a known offset.
        // DCS: ESC P ... ESC \
        // OSC: ESC ] 0 ; title BEL
        // APC: ESC _ ... ESC \
        ReadOnlySpan<byte> chunk = "\x1bPtest\x1b\\\x1b]0;title\x07\x1b_data\x1b\\printable "u8;
        byte[] buf = new byte[TargetBytes];
        int written = 0;
        while (written + chunk.Length <= TargetBytes)
        {
            chunk.CopyTo(buf.AsSpan(written));
            written += chunk.Length;
        }
        while (written < TargetBytes)
        {
            buf[written++] = (byte)' ';
        }
        return buf;
    }
}
