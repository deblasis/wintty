using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace Ghostty.Diagnostics;

/// <summary>
/// Detects a stalled UI thread and leaves evidence behind (#1033).
///
/// When the UI thread blocks on a native lock or a full mailbox queue
/// (the #1036 class), the window goes "not responding" forever and the
/// app writes nothing: the unhandled-exception handlers never run for a
/// hang, and a support report of "it froze" starts from zero. This
/// watchdog closes that gap: a UI-thread heartbeat counter is advanced
/// by a dispatcher timer, and a background thread checks it. If the
/// counter has not moved for the stall window, the watchdog appends a
/// crash.log entry and captures a full minidump of the still-hung
/// process -- the same evidence class that took a three-hour ad-hoc
/// debug session to collect by hand.
///
/// One capture per stall: after firing, the watchdog disarms itself
/// (the UI thread is not going to un-hang on its own; a second dump of
/// the same stacks helps nobody) and keeps a marker so the stall is
/// still visible in the log if the process is later killed.
/// </summary>
internal static class HangWatchdog
{
    /// <summary>How long the UI thread must be silent before this is a
    /// stall worth recording. Long enough that any legitimate long
    /// operation on a dispatcher frame (a synchronous dialog pump, a
    /// slow layout) is not mistaken for one.</summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(20);

    /// <summary>How often the background thread samples the heartbeat.
    /// Two samples per threshold so a stall is caught within ~1.5x the
    /// threshold.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);

    private static long _heartbeat;
    private static int _armed;
    private static DispatcherQueueTimer? _heartbeatTimer;
    private static Thread? _watchThread;

    /// <summary>
    /// Arm the watchdog. Call once, on the UI thread, once the
    /// dispatcher exists -- the heartbeat timer needs it. Idempotent.
    /// </summary>
    public static void Start(DispatcherQueue dispatcher)
    {
        if (Interlocked.Exchange(ref _armed, 1) == 1) return;

        // The heartbeat: a lightweight repeating timer on the UI thread.
        // DispatcherQueueTimer runs on the thread that owns the queue,
        // so the tick IS the proof the thread is pumping.
        _heartbeatTimer = dispatcher.CreateTimer();
        _heartbeatTimer.Interval = TimeSpan.FromMilliseconds(250);
        _heartbeatTimer.Tick += (_, _) => Interlocked.Increment(ref _heartbeat);
        _heartbeatTimer.Start();

        _watchThread = new Thread(WatchLoop)
        {
            Name = "hang-watchdog",
            IsBackground = true,
        };
        _watchThread.Start();
    }

    private static void WatchLoop()
    {
        long lastSeen = Volatile.Read(ref _heartbeat);
        var lastChange = Stopwatch.StartNew();
        while (true)
        {
            Thread.Sleep(SampleInterval);
            var beat = Volatile.Read(ref _heartbeat);
            if (beat != lastSeen)
            {
                lastSeen = beat;
                lastChange.Restart();
                continue;
            }

            if (lastChange.Elapsed < StallThreshold) continue;

            // The UI thread has not pumped for the threshold. Record.
            RecordStall(lastChange.Elapsed);
            return; // one capture per process; see class doc.
        }
    }

    private static void RecordStall(TimeSpan stalledFor)
    {
        var pid = Environment.ProcessId;
        var (logPath, dumpPath) = Paths(pid);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.UtcNow:O} [UI-THREAD STALL]\n" +
                $"The UI thread has not pumped for {stalledFor.TotalSeconds:N0}s; " +
                $"capturing {dumpPath}\n\n");
        }
        catch { /* diagnostics must not throw */ }

        CaptureMinidump(pid, dumpPath);

        try
        {
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.UtcNow:O} [UI-THREAD STALL] minidump " +
                (File.Exists(dumpPath) ? $"written ({new FileInfo(dumpPath).Length:N0} bytes)" : "FAILED") +
                "\n\n");
        }
        catch { }
    }

    private static (string Log, string Dump) Paths(int pid)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Ghostty.Core.AppIdentity.StateDirName);
        return (
            Path.Combine(root, "crash.log"),
            Path.Combine(root, "hangs", $"hang-{pid}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.dmp"));
    }

    // ---- minidump capture ------------------------------------------------

    private const uint MiniDumpWithFullMemory = 0x2;
    private const uint MiniDumpWithHandleData = 0x4;
    private const uint MiniDumpWithFullMemoryInfo = 0x800;
    private const uint MiniDumpWithThreadInfo = 0x1000;

    /// <summary>
    /// dbghelp's MiniDumpWriteDump called in-process. In-process on
    /// purpose: comsvcs' rundll32 dumper writes an Administrators-only
    /// DACL nobody can read without elevating (verified while chasing
    /// #1036), and the dump then can't even be copied out for support.
    /// </summary>
    private static void CaptureMinidump(int pid, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var proc = Process.GetProcessById(pid);
            using var fs = new FileStream(
                path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            // 0x1806: full memory + handle data + full memory info + thread info.
            _ = MiniDumpWriteDump(
                proc.Handle, (uint)pid, fs.SafeFileHandle.DangerousGetHandle(),
                0x1806, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* diagnostics must not throw */ }
    }

    [DllImport("dbghelp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess, uint processId, IntPtr hFile, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
