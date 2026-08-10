using System;
using System.Threading;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    private const int STD_ERROR_HANDLE = -12;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint CREATE_ALWAYS = 2;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    /// <summary>
    /// Persistent GPU diagnostic log path.  Survives reboot so we can
    /// read crash details after a GPU driver crash takes down the machine.
    /// Located next to the executable so it's easy to find.
    /// </summary>
    private static readonly string GpuLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wintty", "gpu.log");

    /// <summary>
    /// Redirect stderr to a file so all diagnostic output (Zig std.log,
    /// C# Console.Error, DX12 debug layer via OutputDebugString) is
    /// persisted to disk.  Called before any native GPU code runs.
    /// </summary>
    private static void RedirectStderrToFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GpuLogPath)!);

            // Open log file with unbuffered writes so data hits disk even if
            // the process is killed by a GPU driver crash.
            var hFile = CreateFileW(
                GpuLogPath,
                GENERIC_WRITE,
                FILE_SHARE_READ,
                IntPtr.Zero,
                CREATE_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (hFile == IntPtr.Zero || hFile == new IntPtr(-1))
                return;

            // Duplicate the handle so Console and native code both see it.
            SetStdHandle(STD_ERROR_HANDLE, hFile);

            // Also redirect managed Console.Error so C# writes go to the file.
            var fs = new FileStream(hFile, FileAccess.Write);
            var writer = new StreamWriter(fs, System.Text.Encoding.UTF8) { AutoFlush = true };
            Console.SetError(writer);

            Console.Error.WriteLine($"=== Wintty GPU log started {DateTime.UtcNow:O} ===");
            Console.Error.WriteLine($"Log file: {GpuLogPath}");
            Console.Error.Flush();
        }
        catch
        {
            // Best effort -- if we can't redirect, we still run normally.
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        // Everything below eventually calls into libghostty, and a
        // Debug-optimized build uses far more stack per call than a release
        // one. Both entry paths need the headroom: CLI actions call
        // ghostty_init straight from here, and the GUI calls it (and
        // ghostty_surface_new) from inside deep XAML callbacks. The default
        // 1MB main-thread stack is not enough for either, so run the whole
        // program on a thread we size ourselves.
        var exitCode = 0;
        var main = new Thread(() => exitCode = MainImpl(args), MainStackSize);
        main.SetApartmentState(ApartmentState.STA);
        main.Start();
        main.Join();
        return exitCode;
    }

    /// <summary>
    /// Stack reserved for the thread the whole program runs on.
    /// </summary>
    private const int MainStackSize = 32 * 1024 * 1024;

    static int MainImpl(string[] args)
    {
        // Persist GPU diagnostics to disk before any native code runs.
        // After a GPU driver crash, the log at %LOCALAPPDATA%\Ghostty\gpu.log
        // survives reboot and tells us exactly what went wrong.
        RedirectStderrToFile();

        // +version is intercepted here, before the libghostty CLI
        // dispatcher. The renderer lives in C# so it can also drive the
        // Version palette dialog with the same output.
        //
        // Still first-argument only. libghostty treats --version as
        // unconditional, so `wintty --font-size=12 --version` detects the
        // action and never runs it, which also silences stderr logging for
        // the session. That is a pre-existing bug and widening the check
        // here would only paper over half of it.
        if (args.Length > 0 &&
            (args[0] == "+version" || args[0] == "--version" ||
             args[0] == "-v" || args[0] == "version"))
        {
            RegisterNativeResolver();
            Environment.Exit(Cli.CliActions.PrintVersion());
        }

        // Does the command line lead with a bare Windows subcommand, e.g.
        // `wintty list-themes`? This is the same call InitWideFromProcess
        // makes to perform the rewrite, over the same input, so the gate
        // below can never open on something the rewrite would then fail to
        // deliver to libghostty.
        var isAlias = Ghostty.Core.Cli.CliAliases.TryRewrite(
            Environment.CommandLine, out _, out _);

        // Help is rendered Windows-side for every spelling, including
        // +help. libghostty's help text names `ghostty`, points at
        // src/config/Config.zig, and explains `open -na Ghostty.app`.
        if (Ghostty.Core.Cli.CliAliases.IsHelpRequest(args, isAlias))
        {
            Console.Out.Write(Ghostty.Core.Cli.CliAliases.RenderHelp(ProgramName()));
            Console.Out.Flush();
            Environment.Exit(0);
        }

        // CLI actions are delegated to libghostty, matching the macOS
        // architecture: ghostty_init parses argv, ghostty_cli_run_action
        // runs the action (if any). If no action, we start the WinUI app.
        //
        // The project uses Exe (console) subsystem so that CLI actions
        // inherit the terminal's console handles natively. This lets
        // Zig's isTty() return true and the Vaxis interactive TUI work.
        // For GUI mode we detach from the console immediately.
        //
        // The StartsWith('+') half of the gate is deliberately unchanged
        // and deliberately not narrowed to known actions: `+bogus` and
        // `+a +b` have to keep reaching libghostty so it can reject them
        // and we can exit with InitFailed, rather than silently opening a
        // window on a typo.
        if (args.Length > 0 && (args[0].StartsWith('+') || isAlias))
        {
            // list-themes (without -tui): try the in-process picker first
            // by sending LIST_THEMES to a running Ghostty app's pipe.
            //
            // Only for a lone argument. The picker cannot honour flags, so
            // `+list-themes --help` used to reach it and exit 0 having
            // printed nothing - and the help text above now sends readers
            // straight at that.
            if (args.Length == 1 &&
                (args[0] == "+list-themes" || args[0] == "list-themes") &&
                TrySendListThemesMessage())
            {
                Environment.Exit(0);
            }

            RegisterNativeResolver();
            InitGhostty();
            RegisterThemeCallback();
            var exitCode = NativeMethods.CliRunAction();
            CleanupThemeCallback();
            if (exitCode >= 0)
                Environment.Exit(exitCode);
        }

        // A bare word that is not a command is a typo, not a config key.
        // libghostty records it as an "invalid field" diagnostic and opens
        // a window anyway; once we tell users wintty takes subcommands,
        // that silence is the wrong answer. Narrow on purpose: paths,
        // flags and -e payloads never match.
        //
        // The isAlias / '+' guard keeps this quiet on the one path that
        // reaches here with a real action: libghostty returning -1 from
        // CliRunAction. Falling through to the GUI is what that did before,
        // and "unknown command 'list-themes'" would be a lie.
        if (args.Length > 0 &&
            !isAlias &&
            !args[0].StartsWith('+') &&
            Ghostty.Core.Cli.CliAliases.LooksLikeCommand(args[0]))
        {
            // stdout, not stderr: RedirectStderrToFile above has already
            // pointed STD_ERROR_HANDLE at %LOCALAPPDATA%\Wintty\gpu.log, so
            // anything written to stderr from here is invisible to the user.
            // The exit code carries the error for scripts.
            Console.Out.WriteLine(
                $"unknown command '{args[0]}'. " +
                $"Run '{ProgramName()} --help' for a list of commands.");
            Console.Out.Flush();
            Environment.Exit(1);
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
    /// Name to print in usage and error text. Derived from the running
    /// binary rather than hardcoded so a rebrand does not leave the CLI
    /// telling users to run a command that no longer exists.
    /// </summary>
    private static string ProgramName()
    {
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        if (string.IsNullOrEmpty(name)) name = Ghostty.Core.AppIdentity.ProductName;
        // Lowercased because that is how the command gets typed, and
        // Windows does not care either way.
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Initialize libghostty from this process's command line.
    ///
    /// The marshalled buffer is intentionally not freed: libghostty keeps a
    /// reference to it and ghostty_cli_run_action reads the args later. The
    /// OS reclaims it on process exit.
    /// </summary>
    private static void InitGhostty()
    {
        // Pass the real WTF-16 command line rather than rebuilding a UTF-8
        // argv. ghostty_init's char** cannot represent WTF-16, so it would
        // ignore what we passed and use the process command line anyway,
        // and reassembling argv would lose unpaired surrogates in paths.
        var result = NativeMethods.InitWideFromProcess();
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
    /// Registered for both the host assembly (Interop.NativeMethods) and
    /// Ghostty.Core, since LibGhosttyBuildInfoBridge lives in the latter.
    /// </summary>
    private static void RegisterNativeResolver()
    {
        static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
        {
            if (!string.Equals(name, "ghostty", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;
            var candidate = Path.Combine(AppContext.BaseDirectory, "native", "ghostty.dll");
            return NativeLibrary.Load(candidate);
        }

        NativeLibrary.SetDllImportResolver(typeof(Interop.NativeMethods).Assembly, Resolve);
        NativeLibrary.SetDllImportResolver(typeof(Ghostty.Core.Version.LibGhosttyBuildInfoBridge).Assembly, Resolve);
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
