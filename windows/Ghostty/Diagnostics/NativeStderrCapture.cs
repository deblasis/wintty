using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Ghostty.Diagnostics;

/// <summary>
/// Route the native side's stderr into a file (#1034).
///
/// libghostty's Zig panics print their message and backtrace to stderr
/// and then abort; in a GUI process there is no stderr (GetStdHandle
/// fails, the writes vanish), so a native abort's entire evidence is
/// the exit code. The libghostty log installer works around this for
/// LOG lines by giving the Zig logger a file, but the default panic
/// handler and a few early-boot writes still go to the raw handle.
///
/// This capture installs the file as the process stderr before any
/// native code runs: open (rotating, capped) the log, SetStdHandle,
/// and every later stderr write -- panic text included -- lands in it.
/// Best-effort by design: a failure to install leaves the app exactly
/// as it was, silent, and that is not worth failing startup over.
/// </summary>
internal static partial class NativeStderrCapture
{
    /// <summary>
    /// Cap on the capture file. Panics are a few KB; anything past this
    /// is a chatty native write we do not need to keep across launches,
    /// so the file truncates at the cap rather than growing forever.
    /// </summary>
    private const long MaxBytes = 8 * 1024 * 1024;

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Ghostty.Core.AppIdentity.StateDirName,
        "native-stderr.log");

    /// <summary>
    /// Install the capture. Call as early as possible in GUI startup --
    /// before libghostty initialization -- so early boot writes and any
    /// later panic both land in the file. Safe to call when a real
    /// stderr exists (terminal launches): it refuses rather than
    /// stealing the developer's console output.
    /// </summary>
    public static void Install()
    {
        try
        {
            // A console-attached launch has a working stderr; leave it
            // alone so `wintty.exe` from a terminal still prints there.
            // GetFileType==DISK/CHAR distinguishes a real handle from
            // the GUI subsystem's null; simplest robust check is the
            // combination .NET already resolved.
            var existing = GetStdHandle(StdErrorHandle);
            if (existing != IntPtr.Zero && !Console.IsErrorRedirected && Console.Error != StreamWriter.Null)
                return;

            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Rotate at the cap: truncate rather than rename -- simpler
            // than rename racing a concurrent writer, and the
            // interesting content (the panic) is always at the tail,
            // which truncation preserves.
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                File.WriteAllText(path, string.Empty);

            // FileShare.ReadWrite so a concurrently-running second
            // window of the same process does not open-exclusively us.
            var fs = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            // Deliberately leaked: the handle must outlive everything
            // that might write to stderr for the process's lifetime.
            bool added = false;
            fs.SafeFileHandle.DangerousAddRef(ref added);
            if (!added) return;
            if (!SetStdHandle(StdErrorHandle, fs.SafeFileHandle.DangerousGetHandle()))
                throw new Win32Exception();
        }
        catch
        {
            // Best-effort by contract: silent on failure.
        }
    }

    private const int StdErrorHandle = -12; // STD_ERROR_HANDLE

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int nStdHandle, IntPtr hHandle);
}
