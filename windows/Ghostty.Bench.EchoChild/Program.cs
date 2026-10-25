// Bench helper: copies stdin to stdout with explicit per-write flush.
using System.Runtime.InteropServices;

const uint STD_INPUT_HANDLE = unchecked((uint)-10);
const uint STD_OUTPUT_HANDLE = unchecked((uint)-11);
const uint STD_ERROR_HANDLE = unchecked((uint)-12);
const uint ENABLE_PROCESSED_INPUT = 0x0001;
const uint ENABLE_LINE_INPUT = 0x0002;
const uint ENABLE_ECHO_INPUT = 0x0004;
const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

string? statusPath = null;
for (int i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--status-path") statusPath = args[i + 1];
}

void WriteStatus(string value)
{
    if (statusPath is null) return;
    try { File.WriteAllText(statusPath, value); } catch { }
}

IntPtr hStdin = NativeMethods.GetStdHandle(STD_INPUT_HANDLE);
IntPtr hStdout = NativeMethods.GetStdHandle(STD_OUTPUT_HANDLE);
IntPtr hStderr = NativeMethods.GetStdHandle(STD_ERROR_HANDLE);
bool inConsole = NativeMethods.GetConsoleMode(hStdin, out uint mode);
bool outConsole = NativeMethods.GetConsoleMode(hStdout, out _);
bool errConsole = NativeMethods.GetConsoleMode(hStderr, out _);

WriteStatus($"CONSOLE={(inConsole ? 1 : 0)}{(outConsole ? 1 : 0)}{(errConsole ? 1 : 0)}");

if (!inConsole || !outConsole || !errConsole)
{
    // Exit code 4 means "no console"; the status file is independent of the
    // standard handles and is what makes this visible before the parent's
    // read deadline. ExitProcess makes it immediate.
    WriteStatus($"NOCON={(inConsole ? 1 : 0)}{(outConsole ? 1 : 0)}{(errConsole ? 1 : 0)}");
    NativeMethods.ExitProcess(4);
}

if (inConsole)
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
        WriteStatus("ERR=setconsolemode");
        NativeMethods.ExitProcess(3);
    }

    // RDY=111: all three standard handles are console handles (the mask is
    // input, output, error). The parent requires the full mask, so a partial
    // handle contract cannot pass by attaching only stdout.
    using (var readyStream = Console.OpenStandardOutput())
    {
        ReadOnlySpan<byte> ready = "RDY=111"u8;
        readyStream.Write(ready);
        readyStream.Flush();
    }
}

try
{
    using var stdin = Console.OpenStandardInput();
    using var stdout = Console.OpenStandardOutput();

    // Explicit Flush per write: ConPTY's stdout buffers partial chunks
    // (e.g., the trailing terminator after a 1 MB payload) which would
    // bench-falsely-positive as latency.
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

    [LibraryImport("kernel32.dll")]
    internal static partial void ExitProcess(uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
