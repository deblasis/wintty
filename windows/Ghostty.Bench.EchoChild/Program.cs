// Copies every byte arriving on stdin to stdout.
// When stdin is a ConPTY terminal, switches to raw mode first so bytes
// arrive immediately without conhost's line-buffering, then writes the
// "RDY" ready sentinel so the parent (Ghostty.Bench) knows conhost's
// VT preamble has been routed and raw mode is active before starting
// to measure. When stdin is a direct pipe (DirectPipeTransport),
// GetConsoleMode fails cleanly so we skip both steps and behave as a
// plain byte-for-byte echo.
//
// Throughput probes require no active participation here: the terminator
// travels through this raw-mode CopyTo byte-for-byte, and under ConPTY
// conhost renders the trailing "~ENDOFBURST_<nonce>~" onto its screen
// buffer and re-emits it on hOutput as part of the next refresh cycle.
// No barrier-response logic lives in EchoChild.
using System.Runtime.InteropServices;

const uint STD_INPUT_HANDLE = unchecked((uint)-10);
const uint ENABLE_PROCESSED_INPUT = 0x0001;
const uint ENABLE_LINE_INPUT = 0x0002;
const uint ENABLE_ECHO_INPUT = 0x0004;
const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

IntPtr hStdin = NativeMethods.GetStdHandle(STD_INPUT_HANDLE);
bool isConPty = NativeMethods.GetConsoleMode(hStdin, out uint mode);

if (isConPty)
{
    // Raw stdin: drop line-buffering and local-echo so a single non-newline
    // byte is delivered immediately to our Read. Keep VT input processing
    // on; parents may feed VT sequences as payload in future throughput work.
    uint rawMode = (mode & ~(ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT | ENABLE_PROCESSED_INPUT))
                   | ENABLE_VIRTUAL_TERMINAL_INPUT;
    if (!NativeMethods.SetConsoleMode(hStdin, rawMode))
    {
        // Hang-fast: if we cannot switch to raw mode the parent would never
        // see writes echoed, so signal failure distinctly instead of entering
        // the copy loop and letting WaitReady hang until timeout.
        using var errStream = Console.OpenStandardOutput();
        ReadOnlySpan<byte> err = "ERR"u8;
        errStream.Write(err);
        errStream.Flush();
        Environment.Exit(3);
    }

    // RDY sentinel: 3 printable ASCII bytes. Conhost renders printable ASCII
    // literally in its VT output stream, so the parent's scan for "RDY"
    // reliably finds these bytes after the preamble. Non-printable C0 bytes
    // (e.g., 0x01) are not guaranteed to survive conhost's screen rendering.
    using (var readyStream = Console.OpenStandardOutput())
    {
        ReadOnlySpan<byte> ready = "RDY"u8;
        readyStream.Write(ready);
        readyStream.Flush();
    }
}

try
{
    using var stdin = Console.OpenStandardInput();
    using var stdout = Console.OpenStandardOutput();

    // Manual copy loop with an explicit Flush after each Write, rather than
    // Stream.CopyTo. Under ConPTY, Console.OpenStandardOutput's stream can
    // hold the final partial chunk of a large burst (e.g., the ~31-byte
    // terminator after a 1 MB throughput payload) in an internal buffer
    // until the next full block or process exit. That delays the parent's
    // terminator observation past the probe's wall-clock budget. DirectPipe
    // does not exhibit this because its anonymous pipe backing is unbuffered.
    // 81920 is Stream.CopyTo's default buffer size; keep parity for round-
    // trip throughput characteristics.
    byte[] buf = new byte[81920];
    int n;
    while ((n = stdin.Read(buf, 0, buf.Length)) > 0)
    {
        stdout.Write(buf, 0, n);
        stdout.Flush();
    }
}
catch (IOException)
{
    // Parent closed the pipe. Normal shutdown.
}

// Source-generated PInvokes. LibraryImport avoids the runtime marshalling
// thunks that DllImport would emit (which break NativeAOT and add a small
// per-call cost in JIT). The signatures here are blittable; only the BOOL
// return needs an explicit MarshalAs because the Win32 BOOL is 4 bytes.
internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr GetStdHandle(uint nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
