using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Ghostty.Core.Diagnostics;
using Ghostty.Interop;

namespace Ghostty.Cli;

/// <summary>
/// Deliberate crash triggers, one per class in the coverage matrix of
/// docs/2026-08-25-crash-reporting-and-diagnostics-design.md.
///
/// Present in every build, including shipped installers. This exists to
/// prove which crash classes the in-process sentry backend can and cannot
/// see, and several of the expected results are deliberately "nothing is
/// captured". A trigger compiled out of Release would leave the one
/// configuration users actually run as the one nobody can verify.
///
/// Each kind uses the exact mechanism its matrix row names. Do not
/// substitute a convenient proxy: the whole value of the harness is that
/// a result says something about the real mechanism.
///
/// The set of kinds is <see cref="CrashKinds"/>, not the switch below.
/// Two front doors reach this: <c>wintty +crash &lt;kind&gt;</c> from
/// <c>Program.cs</c>, and the palette entries built by
/// <c>CrashCommandSource</c>. They share one catalogue and one
/// implementation so a kind cannot exist for one and not the other.
/// </summary>
internal static partial class CrashTrigger
{
    private const uint EXCEPTION_NONCONTINUABLE = 0x1;

    /// <summary>
    /// The exception code <c>native-seh</c> raises. Customer bit set, so it
    /// cannot collide with a system code or with the CLR's 0xE0434352.
    /// </summary>
    /// <remarks>
    /// Named for what it is. It was <c>FACILITY_TEST_EXCEPTION</c>, which
    /// misdirects: this is a whole exception code, not a facility, and the
    /// harness reads the value back as a process exit code.
    /// </remarks>
    private const uint CRASH_TEST_EXCEPTION_CODE = 0xE0000001;

    // The trigger's own exit codes, kept clear of Program.ExitCode, which
    // uses 1, 2 and 3. They collided: ReportFatal returns 3 for anything
    // escaping MainImpl, and 3 here meant "the CLI refused a surface-bound
    // kind", so a developer reading exit 3 could not tell a refusal from a
    // trigger the catch-all had eaten from a failure to load ghostty.dll.
    // 64+ follows sysexits, purely to be somewhere nothing else is.
    private const int ExitUnknownKind = 64;
    private const int ExitNeedsSurface = 65;
    private const int ExitBindingRefused = 66;
    private const int ExitTriggerReturned = 67;
    private const int ExitNoMechanism = 68;

    [LibraryImport("kernel32.dll")]
    private static partial void RaiseException(
        uint dwExceptionCode,
        uint dwExceptionFlags,
        uint nNumberOfArguments,
        IntPtr lpArguments);

    /// <summary>
    /// Runs the named trigger. Never returns for a crashing kind that runs
    /// in-process. Returns a non-zero exit code for an unknown kind, and
    /// for a kind this caller cannot reach.
    /// </summary>
    /// <param name="bindingAction">
    /// Dispatches a libghostty binding action against the active surface,
    /// returning whether it reached one. Null from the CLI, where no
    /// window exists yet, which is exactly why the surface-bound kinds are
    /// refused there rather than silently doing nothing.
    /// </param>
    internal static int Run(string kind, Func<string, bool>? bindingAction = null)
    {
        var entry = CrashKinds.Find(kind);
        if (entry is null)
        {
            Console.Error.WriteLine($"crash-trigger: unknown kind '{kind}'");
            Console.Error.WriteLine($"crash-trigger: kinds: {CrashKinds.Ids}");
            // The subset this front door can run, so the acceptance harness
            // can read the catalogue instead of keeping its own copy. It is a
            // .ps1, so no wiring test can scan it for drift.
            Console.Error.WriteLine($"crash-trigger: cli-kinds: {CrashKinds.CliIds}");
            return ExitUnknownKind;
        }

        Console.Error.WriteLine($"crash-trigger: {entry.Id}");
        Console.Error.Flush();

        if (entry.BindingAction is { } action)
        {
            if (bindingAction is null)
            {
                Console.Error.WriteLine(
                    $"crash-trigger: '{entry.Id}' faults inside libghostty and needs "
                    + "a live surface. Run it from the command palette in a running "
                    + "window; the CLI has no surface to dispatch it against.");
                return ExitNeedsSurface;
            }

            // Reporting a crash for a call that reached no surface is the one
            // outcome a crash probe must not produce: the operator would read
            // the absent envelope as "the reporter missed it".
            if (!bindingAction(action))
            {
                Console.Error.WriteLine(
                    $"crash-trigger: libghostty did not accept '{action}'");
                return ExitBindingRefused;
            }

            // Only crash:main panics on this thread. crash:io and crash:render
            // are mailbox pushes, so control comes back here and the panic
            // lands on the other thread a moment later.
            return 0;
        }

        switch (entry.Id)
        {
            // A genuine native SEH exception, dispatched by Windows through
            // normal exception handling, so sentry's
            // SetUnhandledExceptionFilter WILL see it. This is the one row
            // that must go green for the in-process backend to be worth
            // anything at all.
            //
            // Raised from a thread of its own, for the same reason
            // managed-unhandled is. Measured, not assumed: raising it inline
            // produced no crash at all under CoreCLR, which is what
            // `dotnet build` and `dotnet run` give you. There the runtime
            // wraps the P/Invoke in an SEH frame that translates the exception
            // into a managed SEHException, Program.Main's catch-all caught it,
            // ReportFatal wrote a log, and the process exited 3. The row went
            // red for "no envelope" while the reporter had never been given
            // anything to capture.
            //
            // Naming the runtime matters here. Under NativeAOT, which is what
            // ships, the exception is NOT translated: it goes unhandled and
            // exits 0xE0000001, which is what the harness expects. A reader
            // who believed the CoreCLR behaviour was the AOT one could
            // reasonably conclude the catch-all covers native faults and
            // delete this thread. The thread is correct under both.
            case "native-seh":
                var seh = new Thread(() => RaiseException(
                    CRASH_TEST_EXCEPTION_CODE,
                    EXCEPTION_NONCONTINUABLE,
                    0,
                    IntPtr.Zero));
                seh.Start();
                seh.Join();
                // Unreachable: the translated exception is unhandled on that
                // thread and tears the process down before the join returns.
                return 1;

            // An unhandled managed exception. In NativeAOT this reaches
            // RaiseFailFastException, which bypasses every user-mode
            // handler, so NO envelope is expected.
            //
            // Thrown from a thread of its own, deliberately. Program.Main runs
            // MainImpl inside a catch-all that turns any exception into
            // ReportFatal and a clean exit, so throwing here would exercise
            // the CLI's error path and never be unhandled at all.
            //
            // What that thread does and does NOT reach, stated precisely
            // because an earlier version of this comment got it wrong. A bare
            // Thread is not the XAML UI thread, so
            // Application.UnhandledException in App.xaml.cs never fires for
            // it; only AppDomain.CurrentDomain.UnhandledException can see it,
            // and that one cannot mark the exception handled. So this row
            // measures the AppDomain path and the AOT fail-fast, on both front
            // doors, and it does NOT measure the XAML handler that covers a
            // UI-thread throw in the shipped app. That path has no automated
            // coverage; it needs a throw dispatched onto the DispatcherQueue,
            // which only the palette can do.
            //
            // Join is unreachable: an unhandled exception on any thread tears
            // the process down.
            case "managed-unhandled":
                var unhandled = new Thread(
                    () => throw new InvalidOperationException(
                        "crash-trigger: deliberate unhandled managed exception"));
                unhandled.Start();
                unhandled.Join();
                // Unreachable: the runtime tears the process down before the
                // join returns. Present because the compiler cannot know it.
                return ExitTriggerReturned;

            // Explicit fail-fast. Bypasses all handlers by design.
            case "env-failfast":
                Environment.FailFast("crash-trigger: deliberate FailFast");
                return 1; // unreachable

            // Stack overflow. The thread has no stack left to run a handler
            // on, and sentry's inproc backend sets no stack guarantee and
            // uses no alternate stack.
            case "stack-overflow":
                return Recurse(0);

            // NOT a crash. Throws and catches many times, including a null
            // dereference, which NativeAOT delivers as a hardware fault
            // translated by the runtime's own handler. If sentry's filter is
            // misordered this floods the crash directory. Expected result:
            // zero envelopes, exit 0.
            case "handled-storm":
                for (var i = 0; i < 1000; i++)
                {
                    try
                    {
                        if (i % 2 == 0)
                        {
                            string? nothing = null;
                            _ = nothing!.Length;
                        }
                        else
                        {
                            throw new InvalidOperationException("handled");
                        }
                    }
                    catch (NullReferenceException) { }
                    catch (InvalidOperationException) { }
                }
                Console.Error.WriteLine("crash-trigger: handled-storm survived");
                return 0;

            // A catalogue entry with no binding action and no arm here. A
            // parity test in Ghostty.Tests fails before this can ship, so
            // reaching it means the catalogue was edited and the test was
            // not run; say so rather than crashing in a way that looks like
            // a result.
            default:
                Console.Error.WriteLine(
                    $"crash-trigger: '{entry.Id}' is in the catalogue but has no "
                    + "mechanism here");
                return ExitNoMechanism;
        }
    }

    /// <summary>
    /// Bring libghostty's crash reporting up before a trigger fires.
    /// </summary>
    /// <remarks>
    /// Without this the matrix measures nothing: every crash kind is
    /// intercepted in Program.MainImpl before ghostty_init runs, and
    /// ghostty_init is what reaches crash.init and so sentry_init (see
    /// src/global.zig). A trigger fired first crashes a process that has no
    /// reporter attached, so an absent envelope says nothing about what the
    /// backend can capture.
    ///
    /// The command line passed here is synthetic, and deliberately not this
    /// process's own. libghostty parses argv, and "+crash" is not one of its
    /// actions, so handing it the real command line fails init and exits
    /// before the trigger ever runs.
    ///
    /// sentry_init happens on a background thread (the "sentry-init" thread
    /// in src/crash/sentry.zig), so returning from ghostty_init does not mean
    /// the reporter is armed. Wait for the database directory sentry is
    /// configured with, and give up rather than hang: a trigger that fires a
    /// little early is a visible failed row, a trigger that never fires is a
    /// stuck harness.
    /// </remarks>
    internal static void ArmCrashReporting(TimeSpan timeout)
    {
        // argv[0] only, so there is no action for libghostty to reject.
        // A real Windows command line leads with the executable path, and
        // the args iterator skips that first token before looking for an
        // action. A bare word is not a valid command line in that shape.
        // ProcessPath is nullable, and this is the one path whose entire job
        // is not to fail silently: a null would build `""`, hand libghostty an
        // empty argv[0], and the args iterator would skip a token that is not
        // there.
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Console.Error.WriteLine(
                "crash-trigger: Environment.ProcessPath is unavailable; " +
                "continuing with no reporter attached");
            return;
        }

        var cmdline = "\"" + exe + "\"";
        var buf = Marshal.StringToHGlobalUni(cmdline);
        var status = NativeMethods.InitWide(buf, (UIntPtr)cmdline.Length);
        if (status != 0)
        {
            // Freed only on the failure path. On success libghostty borrows
            // this buffer for the process lifetime (see InitWideFromProcess,
            // which spends eleven lines saying so), so the leak there is
            // required rather than an oversight. Here global.init's errdefer
            // has already unwound and nothing borrows it any more.
            Marshal.FreeHGlobal(buf);
            Console.Error.WriteLine(
                $"crash-trigger: ghostty_init failed (status {status}); " +
                "continuing with no reporter attached");
            return;
        }

        // Ask libghostty, rather than watching for sentry's database
        // directory to appear. That directory is created during init, before
        // the backend's handler is installed, and it outlives the process, so
        // on every run after the first it is already there and the wait
        // returns instantly with the reporter not yet armed. Every "no
        // envelope" row measured that way says nothing.
        // checked: a negative or absurd TimeSpan wraps rather than throwing,
        // and a wrapped timeout silently turns the wait into a no-op, which
        // is the failure this whole method exists to prevent.
        uint timeoutMs;
        checked { timeoutMs = (uint)timeout.TotalMilliseconds; }

        if (NativeMethods.CrashWaitReady(timeoutMs))
        {
            Console.Error.WriteLine("crash-trigger: reporter armed");
            return;
        }

        Console.Error.WriteLine(
            $"crash-trigger: reporter did not arm within {timeout.TotalSeconds:0.#}s; " +
            "triggering anyway, and any absent report below is unproven");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(int depth)
    {
        // The buffer keeps each frame large enough that the overflow arrives
        // promptly, and blocks tail-call optimization from flattening it.
        Span<byte> pad = stackalloc byte[512];
        pad[0] = (byte)depth;
        return Recurse(depth + 1) + pad[0];
    }
}
