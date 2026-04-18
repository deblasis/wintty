namespace Ghostty.Bench.Probes;

public static class Payloads
{
    public const int TargetBytes = 100 * 1024 * 1024;

    // Lazy so a process that only runs round-trip probes never pays the
    // 100 MB alloc, and throughput probes that call these repeatedly
    // share a single buffer instead of churning 100 MB per sample set.
    private static readonly Lazy<ReadOnlyMemory<byte>> _ascii = new(BuildAscii, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ReadOnlyMemory<byte>> _sgr = new(BuildSgr, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ReadOnlyMemory<byte>> _stress = new(BuildStress, LazyThreadSafetyMode.ExecutionAndPublication);

    // 100 MB of uppercase 'A' (0x41). ConPTY's parser sees printables
    // only and takes the fast path; useful as a floor measurement.
    public static ReadOnlyMemory<byte> Ascii100Mb() => _ascii.Value;

    // 100 MB of "\x1b[31mhello\x1b[0m " repeated. Tests SGR parser
    // cost under realistic shell output.
    public static ReadOnlyMemory<byte> Sgr100Mb() => _sgr.Value;

    // 100 MB of dense DCS/OSC/APC sequences interleaved with short
    // printable runs. Pathological ConPTY parser load.
    public static ReadOnlyMemory<byte> Stress100Mb() => _stress.Value;

    private static ReadOnlyMemory<byte> BuildAscii()
    {
        byte[] buf = new byte[TargetBytes];
        Array.Fill(buf, (byte)'A');
        return buf;
    }

    private static ReadOnlyMemory<byte> BuildSgr()
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
        // total is exactly 100 MB.
        while (written < TargetBytes)
        {
            buf[written++] = (byte)'A';
        }
        return buf;
    }

    private static ReadOnlyMemory<byte> BuildStress()
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
