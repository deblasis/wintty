using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ghostty.Interop;

namespace Ghostty;

/// <summary>
/// Custom entry point that wraps the WinUI 3 startup with diagnostic
/// error capture. The XAML-generated Main is suppressed via
/// DISABLE_XAML_GENERATED_MAIN. This is temporary for debugging
/// NativeAOT startup crashes that produce no output.
/// </summary>
public static partial class Program
{
    /// <summary>
    /// Exit codes for Wintty.exe. Distinct values let callers
    /// (launchers, tests, CI, <c>just run-win</c>) tell apart "refused
    /// to start" from "crashed mid-run". CLI actions
    /// (<c>ghostty +list-themes</c> etc.) bypass this scheme and
    /// return whatever the native action produced via
    /// <c>Environment.Exit(exitCode)</c>.
    /// </summary>
    private enum ExitCode
    {
        /// <summary>WinUI message loop returned cleanly, or a CLI
        /// action completed with exit code 0.</summary>
        Success = 0,

        /// <summary>Native / corrupted-state crash (AV, stack overflow).
        /// Not set by our code; this is what Windows returns when an
        /// unhandled SEH exception tears the process down before
        /// managed code sees it. WER captures the minidump under
        /// <c>%LOCALAPPDATA%\CrashDumps\</c>.</summary>
        NativeCrash = 1,

        /// <summary><c>ghostty_init</c> failed. The native library
        /// already wrote the reason to stderr; no config means no
        /// app.</summary>
        InitFailed = 2,

        /// <summary>Unhandled managed exception in the GUI startup
        /// path. <see cref="StartGui"/>'s catch block writes
        /// <c>ghostty-crash.log</c> in <c>AppContext.BaseDirectory</c>.
        /// (A future PR should converge this with the
        /// <c>%LOCALAPPDATA%\Ghostty\crash.log</c> path used by the
        /// App-level unhandled-exception handlers.)</summary>
        ManagedUnhandled = 3,
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleProcessList(
        [Out] uint[] lpdwProcessList,
        uint dwProcessCount);

    [STAThread]
    static int Main(string[] args)
    {
        return MainImpl(args);
    }

    static int MainImpl(string[] args)
    {
        // CLI actions are delegated to libghostty, matching the macOS
        // architecture: ghostty_init parses argv, ghostty_cli_run_action
        // runs the action (if any). If no action, we start the WinUI app.
        //
        // The project uses Exe (console) subsystem so that CLI actions
        // inherit the terminal's console handles natively. This lets
        // Zig's isTty() return true and the Vaxis interactive TUI work.
        // For GUI mode we detach from the console immediately.
        if (args.Length > 0 && args[0].StartsWith('+'))
        {
            // +list-themes (without -tui): try the in-process picker first
            // by sending LIST_THEMES to a running Ghostty app's pipe.
            if (args[0] == "+list-themes" && TrySendListThemesMessage())
                Environment.Exit(0);

            RegisterNativeResolver();
            InitGhostty(args);
            RegisterThemeCallback();
            var exitCode = NativeMethods.CliRunAction();
            CleanupThemeCallback();
            if (exitCode >= 0)
                Environment.Exit(exitCode);
        }

        // Detach from the console before starting WinUI, but ONLY
        // when we are the console's sole owner. Explorer / Start
        // Menu allocates a fresh console for a console-subsystem app
        // and briefly flashes it; that's the console we want to
        // close. A terminal launch (bash, cmd, pwsh) shares the
        // terminal's console with us, and FreeConsole would detach
        // us from that shared console and silently drop every
        // Console.Error.WriteLine below (which is how we lose
        // startup diagnostics and the unhandled-exception dump).
        //
        // GetConsoleProcessList returns >= 2 in the shared case (the
        // parent terminal process counts), exactly 1 in the solo
        // case, and 0 if the probe fails (no attached console). The
        // `<= 1` guard treats a probe failure as solo, which matches
        // the pre-gating behavior and never worse.
        var consoleProcesses = new uint[4];
        var consoleProcessCount = GetConsoleProcessList(
            consoleProcesses,
            (uint)consoleProcesses.Length);
        if (consoleProcessCount <= 1)
            FreeConsole();

        return StartGui();
    }

    /// <summary>
    /// Marshal the managed args array into a C-style argv (null-terminated
    /// UTF-8 strings) and call ghostty_init. The program name "ghostty" is
    /// prepended as argv[0] since .NET's args array omits it.
    ///
    /// The allocated argv is intentionally not freed: ghostty_init stores
    /// the pointers in std.os.argv, and ghostty_cli_run_action reads them
    /// later. The OS reclaims everything on process exit.
    /// </summary>
    private static void InitGhostty(string[] args)
    {
        // Build argv: ["ghostty", args[0], args[1], ...]
        var argc = args.Length + 1;
        var argv = new IntPtr[argc];
        argv[0] = Marshal.StringToCoTaskMemUTF8("ghostty");
        for (int i = 0; i < args.Length; i++)
            argv[i + 1] = Marshal.StringToCoTaskMemUTF8(args[i]);

        var argvPtr = Marshal.AllocCoTaskMem(IntPtr.Size * argc);
        Marshal.Copy(argv, 0, argvPtr, argc);

        var result = NativeMethods.Init((UIntPtr)argc, argvPtr);
        if (result != 0)
        {
            // ghostty_init failed (e.g. invalid action). The Zig
            // code logs to stderr. Distinct exit code per the
            // ExitCode enum above.
            Environment.Exit((int)ExitCode.InitFailed);
        }
    }

    private static System.IO.Pipes.NamedPipeClientStream? _themePipe;
    private static StreamWriter? _themePipeWriter;

    private static unsafe void RegisterThemeCallback()
    {
        // Find the running Ghostty app's pipe. The pipe name includes
        // the PID, so we scan for ghostty-theme-preview-* pipes.
        // If no running app is found, the callback is a no-op.
        var pipeName = FindThemePreviewPipe();
        if (pipeName is not null)
        {
            try
            {
                _themePipe = new System.IO.Pipes.NamedPipeClientStream(
                    ".", pipeName,
                    System.IO.Pipes.PipeDirection.Out);
                _themePipe.Connect(1000); // 1s timeout
                _themePipeWriter = new StreamWriter(_themePipe) { AutoFlush = true };
            }
            catch
            {
                _themePipe?.Dispose();
                _themePipe = null;
                _themePipeWriter = null;
            }
        }

        NativeMethods.CliSetThemeCallback((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, void>)&OnThemeChanged);
    }

    private static void CleanupThemeCallback()
    {
        NativeMethods.CliSetThemeCallback(IntPtr.Zero);
        // Closing the pipe without a CONFIRM message tells the server
        // to revert to the original theme.
        _themePipeWriter?.Dispose();
        _themePipe?.Dispose();
        _themePipeWriter = null;
        _themePipe = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnThemeChanged(IntPtr namePtr, byte confirmed)
    {
        var name = Marshal.PtrToStringUTF8(namePtr);
        if (name is null || _themePipeWriter is null) return;

        try
        {
            _themePipeWriter.WriteLine(confirmed != 0 ? $"CONFIRM:{name}" : $"PREVIEW:{name}");
        }
        catch
        {
            // Pipe broken -- running app may have closed.
        }
    }

    /// <summary>
    /// Try to send LIST_THEMES to a running Ghostty app's pipe.
    /// Returns true if the message was sent successfully.
    /// </summary>
    private static bool TrySendListThemesMessage()
    {
        var pipeName = FindThemePreviewPipe();
        if (pipeName is null) return false;

        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.Out);
            pipe.Connect(1000);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("LIST_THEMES");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindThemePreviewPipe()
    {
        // Look for a running Wintty process and try its pipe name.
        // The pipe is named ghostty-theme-preview-{PID}.
        // Match the assembly name from windows/Ghostty/Ghostty.csproj
        // so this stays in sync if the binary is ever renamed again.
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("Wintty");
            foreach (var proc in procs)
            {
                using (proc)
                {
                    if (proc.Id == Environment.ProcessId) continue;
                    var candidate = $"ghostty-theme-preview-{proc.Id}";
                    // Check if the pipe exists by trying the well-known path.
                    if (File.Exists($@"\\.\pipe\{candidate}"))
                        return candidate;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Register the native DLL resolver so LibraryImport("ghostty") finds
    /// native/ghostty.dll. Mirrors the resolver in App.xaml.cs but runs
    /// before WinUI is initialized, enabling CLI-path P/Invoke calls.
    /// </summary>
    private static void RegisterNativeResolver()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(Interop.NativeMethods).Assembly,
            (name, assembly, path) =>
            {
                if (!string.Equals(name, "ghostty", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;
                var candidate = Path.Combine(AppContext.BaseDirectory, "native", "ghostty.dll");
                return NativeLibrary.Load(candidate);
            });
    }

    private static int StartGui()
    {
        try
        {
            Console.Error.WriteLine("[Ghostty] Program.Main entered");
            Console.Error.Flush();

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Console.Error.WriteLine("[Ghostty] ComWrappers initialized");
            Console.Error.Flush();

            Microsoft.UI.Xaml.Application.Start(p =>
            {
                Console.Error.WriteLine("[Ghostty] Application.Start callback entered");
                Console.Error.Flush();

                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);

                Console.Error.WriteLine("[Ghostty] Creating App instance");
                Console.Error.Flush();

                new App();

                Console.Error.WriteLine("[Ghostty] App instance created");
                Console.Error.Flush();
            });

            return 0;
        }
        catch (Exception ex)
        {
            var msg = $"[Ghostty] FATAL: {ex}";
            Console.Error.WriteLine(msg);
            Console.Error.Flush();

            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "ghostty-crash.log");
                File.WriteAllText(logPath, msg);
            }
            catch { /* best effort */ }

            return (int)ExitCode.ManagedUnhandled;
        }
    }
}
