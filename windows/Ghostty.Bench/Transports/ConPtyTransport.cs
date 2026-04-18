using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Ghostty.Bench.Transports;

// Spawns a child attached to a pseudo-console (ConPTY) via inbox
// kernel32!CreatePseudoConsole. Measurement-only. Does not handle
// resize, signals, or subprocess inheritance -- those are #112's
// concerns.
//
// Handle discipline (critical):
//   CreatePipe twice -> four handles.
//   Terminal-side handles (inputRead, outputWrite) get passed to
//   CreatePseudoConsole, which dup's them internally. We MUST close
//   our copies immediately after CreatePseudoConsole returns,
//   otherwise the child's stdout pipe never gets EOF on exit and
//   the reader hangs forever.
// Audited against microsoft/terminal/samples/ConPTY/MiniTerm on 2026-04-17.
[SupportedOSPlatform("windows")]
public sealed class ConPtyTransport : ITransport
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly FileStream _inputStream;
    private readonly FileStream _outputStream;
    private readonly IntPtr _hpcon;
    private readonly IntPtr _attrList;
    private readonly IntPtr _procHandle;
    private int _disposed;

    public ConPtyTransport(string childExePath, short cols = 80, short rows = 24)
    {
        if (!File.Exists(childExePath))
        {
            throw new TransportException($"child binary not found: {childExePath}");
        }

        // Input pipe: terminal reads, we write.
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
        {
            throw new TransportException($"CreatePipe (input) failed: 0x{Marshal.GetLastWin32Error():X8}");
        }

        // Output pipe: we read, terminal writes.
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new TransportException($"CreatePipe (output) failed: 0x{Marshal.GetLastWin32Error():X8}");
        }

        int hr = CreatePseudoConsole(new Coord(cols, rows), inputRead, outputWrite, 0, out _hpcon);
        inputRead.Dispose();   // close our copy; ConPTY dup'd it.
        outputWrite.Dispose(); // ditto.
        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new TransportException($"CreatePseudoConsole failed: 0x{hr:X8}");
        }

        _inputWrite = inputWrite;
        _outputRead = outputRead;

        IntPtr lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize); // first call returns required size
        _attrList = Marshal.AllocHGlobal(lpSize);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref lpSize))
        {
            int initErr = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(_attrList);
            ClosePseudoConsole(_hpcon);
            _inputWrite.Dispose();
            _outputRead.Dispose();
            throw new TransportException($"InitializeProcThreadAttributeList failed: 0x{initErr:X8}");
        }

        if (!UpdateProcThreadAttribute(
                _attrList,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hpcon,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            ClosePseudoConsole(_hpcon);
            _inputWrite.Dispose();
            _outputRead.Dispose();
            throw new TransportException($"UpdateProcThreadAttribute failed: 0x{err:X8}");
        }

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        si.lpAttributeList = _attrList;

        string cmdLine = $"\"{childExePath}\"";

        if (!CreateProcess(
                null,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                null,
                ref si,
                out var pi))
        {
            int err = Marshal.GetLastWin32Error();
            DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            ClosePseudoConsole(_hpcon);
            _inputWrite.Dispose();
            _outputRead.Dispose();
            throw new Win32Exception(err, $"CreateProcess failed: 0x{err:X8}");
        }

        CloseHandle(pi.hThread);
        _procHandle = pi.hProcess;

        _inputStream = new FileStream(_inputWrite, FileAccess.Write, bufferSize: 1, isAsync: false);
        _outputStream = new FileStream(_outputRead, FileAccess.Read, bufferSize: 4096, isAsync: false);
    }

    public Stream Input => _inputStream;
    public Stream Output => _outputStream;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Dispose must not throw. Each call site below is individually
        // wrapped so a failure in one step (e.g. a double-free racing with
        // a crashing child) does not skip the remaining cleanup.
        //
        // Order matters: ClosePseudoConsole first, then streams. The
        // reverse order can hang on some Windows builds.
        try { ClosePseudoConsole(_hpcon); } catch { }

        try
        {
            if (_procHandle != IntPtr.Zero)
            {
                uint waitResult = WaitForSingleObject(_procHandle, 5000);
                if (waitResult != 0) // WAIT_OBJECT_0 = 0
                {
                    TerminateProcess(_procHandle, 1);
                }
                CloseHandle(_procHandle);
            }
        }
        catch { }

        try { _inputStream.Dispose(); } catch { }
        try { _outputStream.Dispose(); } catch { }

        if (_attrList != IntPtr.Zero)
        {
            try { DeleteProcThreadAttributeList(_attrList); } catch { }
            try { Marshal.FreeHGlobal(_attrList); } catch { }
        }
    }

    // Drains conhost's output pipe until the "RDY" sentinel emitted by
    // Ghostty.Bench.EchoChild is seen, then returns. Everything up to and
    // including "RDY" is discarded; subsequent Read calls on Output start
    // clean. Throws TransportException on timeout or pipe EOF.
    //
    // Implementation note: we rely on conhost emitting the child's printable
    // ASCII bytes literally in its VT output stream (true for any sequence
    // of non-control characters at known cursor positions). See the spec for
    // why "RDY" is safe and "0x01" (the pre-8762af4ed sentinel) was not.
    public void WaitReady(TimeSpan timeout)
    {
        ReadOnlySpan<byte> ready = "RDY"u8;
        long deadline = Stopwatch.GetTimestamp()
                        + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        // Window holds the accumulated drain bytes. Conhost's preamble is
        // small (< 64 bytes in practice) plus the 3-byte RDY, so a List<byte>
        // with modest reserve is fine; this is not a hot path.
        var window = new List<byte>(256);
        Span<byte> buf = stackalloc byte[256];

        while (Stopwatch.GetTimestamp() < deadline)
        {
            int n = _outputStream.Read(buf);
            if (n <= 0)
            {
                throw new TransportException(
                    "ConPty output stream EOF before ready sentinel");
            }

            window.AddRange(buf[..n]);

            if (CollectionsMarshal.AsSpan(window).IndexOf(ready) >= 0)
            {
                return;
            }
        }

        throw new TransportException(
            $"ConPty child did not emit ready sentinel within {timeout.TotalSeconds:F1}s");
    }

    // --- Win32 types and imports ---

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        public readonly short X;
        public readonly short Y;
        public Coord(short x, short y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        Coord size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
