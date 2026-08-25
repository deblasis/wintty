using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ghostty.Cli;

/// <summary>
/// Deliberate crash triggers, one per class in the coverage matrix of
/// docs/2026-08-25-crash-reporting-and-diagnostics-design.md.
///
/// Compiled out of Release entirely: this exists to prove which crash
/// classes the in-process sentry backend can and cannot see, and several
/// of the expected results are deliberately "nothing is captured".
///
/// Each kind uses the exact mechanism its matrix row names. Do not
/// substitute a convenient proxy: the whole value of the harness is that
/// a result says something about the real mechanism.
/// </summary>
internal static partial class CrashTrigger
{
#if DEBUG
    private const uint EXCEPTION_NONCONTINUABLE = 0x1;
    private const uint FACILITY_TEST_EXCEPTION = 0xE0000001;

    [LibraryImport("kernel32.dll")]
    private static partial void RaiseException(
        uint dwExceptionCode,
        uint dwExceptionFlags,
        uint nNumberOfArguments,
        IntPtr lpArguments);

    /// <summary>
    /// Runs the named trigger. Never returns for a crashing kind.
    /// Returns a non-zero exit code for an unknown kind.
    /// </summary>
    internal static int Run(string kind)
    {
        Console.Error.WriteLine($"crash-trigger: {kind}");
        Console.Error.Flush();

        switch (kind)
        {
            // A genuine native SEH exception, dispatched by Windows through
            // normal exception handling, so sentry's
            // SetUnhandledExceptionFilter WILL see it. This is the one row
            // that must go green for the in-process backend to be worth
            // anything at all.
            case "native-seh":
                RaiseException(
                    FACILITY_TEST_EXCEPTION,
                    EXCEPTION_NONCONTINUABLE,
                    0,
                    IntPtr.Zero);
                return 1; // unreachable

            // An unhandled managed exception. In NativeAOT this reaches
            // RaiseFailFastException, which bypasses every user-mode
            // handler, so NO envelope is expected. The existing handlers in
            // App.xaml.cs are what capture this class, with a managed stack
            // trace, which is more useful than a dump would be.
            case "managed-unhandled":
                throw new InvalidOperationException(
                    "crash-trigger: deliberate unhandled managed exception");

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

            default:
                Console.Error.WriteLine(
                    $"crash-trigger: unknown kind '{kind}'");
                return 2;
        }
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
#else
    internal static int Run(string kind)
    {
        Console.Error.WriteLine(
            "crash-trigger is compiled out of Release builds");
        return 2;
    }
#endif
}
