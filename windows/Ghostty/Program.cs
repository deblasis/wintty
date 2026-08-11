using System;
using System.Threading;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ghostty.Core;
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

        /// <summary>Unusable command line: a bare word that is not a known
        /// subcommand. Deliberately shares its value with
        /// <see cref="NativeCrash"/>, because 1 is the conventional usage
        /// exit and changing it would break callers. The two are still
        /// distinguishable in practice - this one always prints
        /// "unknown command '...'" to stderr first, a native teardown prints
        /// nothing and leaves a minidump.</summary>
        UsageError = 1,

        /// <summary><c>ghostty_init</c> failed; no config means no app.
        /// libghostty explains argument errors itself; the status and this
        /// exit code come from <see cref="InitGhostty"/>.</summary>
        InitFailed = 2,

        /// <summary>Unhandled managed exception in the GUI startup
        /// path. <see cref="StartGui"/>'s catch block writes
        /// <c>ghostty-crash.log</c> in <c>AppContext.BaseDirectory</c>.
        /// (A future PR should converge this with the
        /// <c>%LOCALAPPDATA%\Wintty\crash.log</c> path used by the
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private const int STD_ERROR_HANDLE = -12;
    /// <summary>
    /// Append-only write access. Requested INSTEAD of <c>GENERIC_WRITE</c>:
    /// a handle that carries <c>FILE_APPEND_DATA</c> without
    /// <c>FILE_WRITE_DATA</c> makes every write an atomic append at the end of
    /// the file, whatever offset the writer thinks it is at. Two writers share
    /// this handle - libghostty streaming through the OS file pointer, and the
    /// managed <see cref="Console.Error"/> writer - and that is what keeps them
    /// from landing on top of each other.
    /// </summary>
    private const uint FILE_APPEND_DATA = 0x00000004;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint CREATE_ALWAYS = 2;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    /// <summary>
    /// Persistent GPU diagnostic log path.  Survives reboot so we can
    /// read crash details after a GPU driver crash takes down the machine.
    /// </summary>
    private static readonly string GpuLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wintty", "gpu.log");

    /// <summary>
    /// Set to any value to force the <see cref="GpuLogPath"/> redirect on for
    /// a CLI action, which otherwise keeps the terminal's stderr. This is an
    /// environment variable rather than a command line flag because
    /// libghostty parses the process command line itself and rejects any
    /// option it does not recognize.
    /// </summary>
    private const string GpuLogEnvVar = "WINTTY_GPU_LOG";

    private static bool _stderrRedirected;

    /// <summary>
    /// The <see cref="Console.Error"/> writer from before
    /// <see cref="RedirectStderrToFile"/> replaced it, or null if the
    /// redirect never ran. Fatal startup failures are teed here so a launch
    /// from a terminal shows the reason instead of just an exit code.
    /// </summary>
    private static TextWriter? _preRedirectStderr;

    /// <summary>
    /// <see cref="Console.Error"/> as it stood at the very top of
    /// <see cref="MainImpl"/>. Captured there rather than inside
    /// <see cref="RedirectStderrToFile"/> because the property binds lazily -
    /// its first read resolves <c>GetStdHandle</c>, so any
    /// <c>Console.Error</c> write added above the redirect would otherwise
    /// decide what this points at. Capturing at the entry point makes it
    /// structural instead of dependent on statement order.
    /// </summary>
    private static TextWriter? _terminalStderr;

    /// <summary>
    /// Redirect stderr to a file so all diagnostic output (Zig std.log,
    /// C# Console.Error, DX12 debug layer via OutputDebugString) is
    /// persisted to disk.  Called before any native GPU code runs.
    ///
    /// Idempotent: the GUI path and the <see cref="GpuLogEnvVar"/> opt-in can
    /// both reach it, and re-opening would truncate the log we just wrote.
    /// </summary>
    private static void RedirectStderrToFile()
    {
        if (_stderrRedirected) return;

        // Set before the attempt, not after: a redirect that failed once is
        // not going to succeed on a retry, and reopening would truncate.
        _stderrRedirected = true;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GpuLogPath)!);

            // AutoFlush on the writer below pushes every line to the OS, so a
            // GPU driver crash loses at most a partial line. This is not
            // write-through (no FILE_FLAG_WRITE_THROUGH), so the OS cache can
            // still lose data if the machine itself goes down.
            var hFile = CreateFileW(
                GpuLogPath,
                FILE_APPEND_DATA,
                FILE_SHARE_READ,
                IntPtr.Zero,
                CREATE_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (hFile == IntPtr.Zero || hFile == InvalidHandleValue)
            {
                // Console.Error is still the terminal here, so say so rather
                // than losing every diagnostic and the tee in silence.
                Console.Error.WriteLine(
                    $"{AppIdentity.LogTag} could not open {GpuLogPath}: " +
                    Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()));
                Console.Error.Flush();
                return;
            }

            // Point the process stderr handle at the file so native writes
            // (Zig std.log, the DX12 debug layer) land there too. Console and
            // native code share this one handle; nothing is duplicated.
            if (!SetStdHandle(STD_ERROR_HANDLE, hFile))
            {
                // Managed stderr would go to the file while native stderr kept
                // the console: a split that reads as "the log is just missing
                // every Zig line". Leave both where they are, and close the
                // handle - FILE_SHARE_READ means holding it would lock the log
                // against the next run's CreateFileW.
                CloseHandle(hFile);
                return;
            }

            // Only now is there something to tee to. `_terminalStderr` was
            // captured at the top of MainImpl, before anything could bind
            // Console.Error to a redirected handle; it is TextWriter.Null on a
            // launch with no console, which discards harmlessly.
            _preRedirectStderr = _terminalStderr;

            // Also redirect managed Console.Error so C# writes go to the file.
            //
            // OpenStandardError, not a FileStream over the raw handle. The
            // append-only access above already makes the two writers' offsets
            // irrelevant, so this is the second line of defence rather than
            // the fix; it also sidesteps the ownership question, since nothing
            // here wraps the raw HANDLE. That handle is deliberately leaked to
            // process exit because SetStdHandle only stores the value.
            //
            // No BOM: native writes through the same handle are raw UTF-8, so
            // a preamble would only appear if the managed side happened to
            // write first.
            var writer = new StreamWriter(
                Console.OpenStandardError(),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            { AutoFlush = true };
            Console.SetError(writer);

            Console.Error.WriteLine(
                $"=== {AppIdentity.ProductName} GPU log started {DateTime.UtcNow:O} ===");
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
        // Wrapped because MainImpl now makes the process's first P/Invoke on
        // its own frame: InitGhostty moved here from ConfigService, which used
        // to run inside Application.Start where StartGui's catch and
        // App.UnhandledException could still report a failure. A missing
        // native/ghostty.dll would otherwise unwind this delegate unhandled
        // and abort with an exit code that is none of the ExitCode values
        // callers are told to discriminate on.
        var exitCode = 0;
        var main = new Thread(
            () =>
            {
                try
                {
                    exitCode = MainImpl(args);
                }
                catch (Exception ex)
                {
                    exitCode = ReportFatal(ex);
                }
            },
            MainStackSize);
        main.SetApartmentState(ApartmentState.STA);
        main.Start();
        main.Join();
        return exitCode;
    }

    /// <summary>
    /// Last-resort reporter for an exception that escaped <see cref="MainImpl"/>.
    /// Writes to stderr, tees to the terminal when stderr has been redirected,
    /// and drops a crash log next to the binary, mirroring what
    /// <see cref="StartGui"/>'s catch does for the GUI path.
    /// </summary>
    private static int ReportFatal(Exception ex)
    {
        var message = $"{AppIdentity.LogTag} FATAL: {ex}";

        try
        {
            Console.Error.WriteLine(message);
            Console.Error.Flush();
            _preRedirectStderr?.WriteLine(message);
            _preRedirectStderr?.Flush();
        }
        catch
        {
            // Reporting must not throw over the top of the real failure.
        }

        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "ghostty-crash.log"),
                $"{DateTime.UtcNow:O}\n{ex}\n");
        }
        catch
        {
            // Best effort.
        }

        return (int)ExitCode.ManagedUnhandled;
    }

    /// <summary>
    /// Stack reserved for the thread the whole program runs on.
    /// </summary>
    private const int MainStackSize = 32 * 1024 * 1024;

    static int MainImpl(string[] args)
    {
        // Resolve `ghostty` to native/ghostty.dll for every entry path,
        // before anything can P/Invoke into it. This is deliberately the
        // only registration in the process: SetDllImportResolver throws
        // InvalidOperationException on the second call for an assembly, and
        // App's static constructor used to register this same assembly
        // again, so a `+action` run that found no action and fell through to
        // the GUI died in that static constructor rather than starting.
        // Before anything can write to Console.Error and bind it to whatever
        // GetStdHandle returns at that moment. See _terminalStderr.
        _terminalStderr = Console.Error;

        RegisterNativeResolver();

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
            // A CLI action keeps the terminal's stderr, so that libghostty's
            // own argument errors reach the user: `wintty +bogus` reports
            // InvalidAction, `wintty +list-themes +show-config` reports
            // MultipleActions. Redirecting first turned both into a bare exit
            // code with the reason buried in a log file.
            //
            // WINTTY_GPU_LOG opts back into the file, which is how you capture
            // diagnostics from a CLI run that has no console to print to (a
            // scheduled task, a Start Menu launch).
            if (Environment.GetEnvironmentVariable(GpuLogEnvVar) is not null)
                RedirectStderrToFile();

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
            Console.Error.WriteLine(
                $"unknown command '{args[0]}'. " +
                $"Run '{ProgramName()} --help' for a list of commands.");
            Console.Error.Flush();
            Environment.Exit((int)ExitCode.UsageError);
        }

        // Persist GPU diagnostics to disk before any native code runs. After
        // a GPU driver crash the log at %LOCALAPPDATA%\Wintty\gpu.log survives
        // reboot and tells us exactly what went wrong. Nothing above this line
        // touches the GPU, so starting here costs the GUI no coverage.
        //
        // Also covers the case where the block above found no action to run
        // and fell through to the GUI; the call is idempotent.
        RedirectStderrToFile();

        // Initialize libghostty here, on MainImpl's own frame, rather than
        // from ConfigService's constructor. Two reasons:
        //
        // A failed init exits the process. From ConfigService that ran inside
        // Application.Start's initialization callback - an STA COM call - and
        // it bypassed StartGui's catch, which is the only thing on this path
        // that writes ghostty-crash.log. Exiting from a top-level frame avoids
        // both.
        //
        // It also puts the one process-lifetime decision at the composition
        // root instead of partway down the object graph, so a service in
        // Services/ no longer has to reach back into the entry point to be
        // constructible.
        //
        // Still after the redirect above: the log is the only sink a Start
        // Menu launch has. InitGhostty tees its fatal line to the terminal.
        InitGhostty();

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
        if (string.IsNullOrEmpty(name)) name = AppIdentity.ProductName;
        // Lowercased because that is how the command gets typed, and
        // Windows does not care either way.
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Whether <see cref="InitGhostty"/> has already initialized libghostty.
    /// </summary>
    /// <remarks>
    /// Both startup paths call it and both run on the thread <see cref="Main"/>
    /// creates, which goes on to become the UI thread, so a plain field is
    /// enough. libghostty refuses a second init, and it reports that refusal
    /// with the same status as a real failure, so without this gate a
    /// double-init would be indistinguishable from a fatal one.
    /// </remarks>
    private static bool _ghosttyInitialized;

    /// <summary>
    /// Managed thread that ran <see cref="InitGhostty"/>, for the tripwire in
    /// that method. Zero until it is first entered.
    /// </summary>
    /// <summary>
    /// Serializes <see cref="InitGhostty"/>. libghostty allows one init per
    /// process and reports a refused second call with the same status as a
    /// real failure, so a second caller must wait for the first to finish
    /// rather than race past a half-set flag.
    /// </summary>
    private static readonly Lock InitLock = new();

    /// <summary>
    /// Whether <see cref="InitGhostty"/> has completed. Callers that depend on
    /// libghostty global state assert on this rather than initializing it
    /// themselves; <see cref="MainImpl"/> owns that decision.
    /// </summary>
    internal static bool IsGhosttyInitialized => _ghosttyInitialized;

    /// <summary>
    /// Initialize libghostty from this process's command line, exiting the
    /// process if it fails. A second call is a no-op.
    ///
    /// This is the single init entry point, and both startup paths call it
    /// from <see cref="MainImpl"/> - the CLI branch before dispatching an
    /// action, the GUI branch before <see cref="StartGui"/>. It must run
    /// before any export that touches global state (config, app, surface).
    /// Exports that only read static build data, such as ghostty_build_info
    /// behind +version, do not need it.
    ///
    /// The marshalled buffer is intentionally not freed: libghostty keeps a
    /// reference to it and ghostty_cli_run_action reads the args later. The
    /// OS reclaims it on process exit.
    /// </summary>
    internal static void InitGhostty()
    {
        // A second caller has to wait for the first to finish, not merely
        // observe that it started: libghostty refuses a second init, and
        // reports that refusal with the same status as a real failure, so a
        // racing caller that got past a half-set flag would exit a healthy
        // process with InitFailed. The lock is the whole fix and is cheaper
        // than the tripwire it replaces, which only made the violation
        // visible in builds that ship with assertions enabled.
        lock (InitLock)
        {
            if (_ghosttyInitialized) return;

        // Pass the real WTF-16 command line rather than rebuilding a UTF-8
        // argv. ghostty_init's char** cannot represent WTF-16, so it would
        // ignore what we passed and use the process command line anyway,
        // and reassembling argv would lose unpaired surrogates in paths.
        var result = NativeMethods.InitWideFromProcess();
        if (result != 0)
        {
            // ghostty_init failed (e.g. invalid action). There is no
            // degraded mode to continue in: global.init runs its errdefer on
            // the way out, which deinits the allocator and the I/O instance
            // every later export depends on.
            //
            // libghostty explains the argument-parsing errors itself, via
            // global.reportInitError writing straight to stderr. This line
            // adds what that cannot: the native status, the exit code about
            // to be used, and some signal for the errors it has no text for
            // (global logging defaults to stderr = false in the lib
            // artifact, so their std.log.err reaches only an embedder that
            // registered a log callback).
            //
            // A CLI action still owns the terminal's stderr, so one write
            // reaches the user. The GUI path has already pointed stderr at the
            // log file, and libghostty's own explanation goes to that same
            // redirected handle, so a terminal launch would otherwise show
            // nothing but an exit code. Tee to the pre-redirect writer and name
            // the log, which is where the rest of the detail is.
            var message =
                $"{AppIdentity.LogTag} FATAL: " +
                $"ghostty_init failed (status {result}), exiting with " +
                $"{(int)ExitCode.InitFailed} ({nameof(ExitCode.InitFailed)})";

            // Terminal first, and each in its own try: a human is watching
            // that one, and a throw from the log write (disk full, or a
            // handle FreeConsole invalidated) must not take it down with it.
            // Neither may escape either - this method exits with InitFailed,
            // and an exception here would unwind into Main's handler and
            // turn that into ManagedUnhandled, which is the other code the
            // ExitCode doc tells callers to discriminate on.
            try
            {
                if (_preRedirectStderr is { } terminal)
                {
                    terminal.WriteLine(message);
                    terminal.WriteLine($"{AppIdentity.LogTag} See {GpuLogPath} for details.");
                    terminal.Flush();
                }
            }
            catch { /* reporting must not change the exit code */ }

            try
            {
                Console.Error.WriteLine(message);
                Console.Error.Flush();
            }
            catch { /* as above */ }

            Environment.Exit((int)ExitCode.InitFailed);
            }

            _ghosttyInitialized = true;
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
    /// native/ghostty.dll. The sole registration in the process, called
    /// first thing in <see cref="MainImpl"/> so every path (CLI and GUI)
    /// can P/Invoke. Registered for both the host assembly
    /// (Interop.NativeMethods) and Ghostty.Core, since
    /// LibGhosttyBuildInfoBridge lives in the latter.
    ///
    /// libghostty.dll lives in a `native/` subdirectory next to this
    /// assembly so its filename (ghostty.dll) does not collide with our own
    /// managed Ghostty.dll on case-insensitive filesystems, which is why the
    /// name has to be resolved by hand at all.
    ///
    /// JIT and framework-dependent builds only. A PublishAot build sets
    /// DirectPInvoke for "ghostty" and links ghostty-static.lib, which binds
    /// every LibraryImport at link time, so the runtime never consults a
    /// resolver there and this method has no effect.
    /// </summary>
    private static void RegisterNativeResolver()
    {
        static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
        {
            if (!string.Equals(name, "ghostty", StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;
            // AppContext.BaseDirectory works in all deployment modes
            // (framework-dependent, single-file, Native AOT).
            // assembly.Location returns empty under single-file and AOT.
            var candidate = Path.Combine(AppContext.BaseDirectory, "native", "ghostty.dll");

            // TryLoad, not Load: throwing from inside a resolver surfaces at
            // whichever P/Invoke happened to be first, which on the CLI paths
            // is an uncaught DllNotFoundException and a raw CLR stack trace.
            // Returning zero lets the runtime raise its own error, and the
            // line below says where we actually looked.
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;

            Console.Error.WriteLine(
                $"{AppIdentity.LogTag} FATAL: could not load {candidate}");
            Console.Error.Flush();
            return IntPtr.Zero;
        }

        // Guarded rather than two bare calls: SetDllImportResolver throws on
        // the second registration for an assembly, so folding Ghostty.Core
        // into the host assembly would otherwise resurrect the exact crash
        // this single-registration setup exists to remove, on line one of
        // MainImpl where nothing can report it yet.
        var host = typeof(Interop.NativeMethods).Assembly;
        var core = typeof(Ghostty.Core.Version.LibGhosttyBuildInfoBridge).Assembly;
        NativeLibrary.SetDllImportResolver(host, Resolve);
        if (!ReferenceEquals(core, host))
            NativeLibrary.SetDllImportResolver(core, Resolve);
    }

    private static int StartGui()
    {
        try
        {
            Console.Error.WriteLine($"{AppIdentity.LogTag} Program.Main entered");
            Console.Error.Flush();

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Console.Error.WriteLine($"{AppIdentity.LogTag} ComWrappers initialized");
            Console.Error.Flush();

            Microsoft.UI.Xaml.Application.Start(p =>
            {
                Console.Error.WriteLine($"{AppIdentity.LogTag} Application.Start callback entered");
                Console.Error.Flush();

                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);

                Console.Error.WriteLine($"{AppIdentity.LogTag} Creating App instance");
                Console.Error.Flush();

                new App();

                Console.Error.WriteLine($"{AppIdentity.LogTag} App instance created");
                Console.Error.Flush();
            });

            return 0;
        }
        catch (Exception ex)
        {
            // Same reporting as an escape from MainImpl, so the GUI path also
            // gets the terminal tee instead of only the log.
            return ReportFatal(ex);
        }
    }
}
