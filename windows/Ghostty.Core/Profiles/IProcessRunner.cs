using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles;

/// <summary>
/// How a process run ended. ExitCode alone cannot tell "never spawned"
/// apart from "spawned and was killed on timeout" -- both report -1 --
/// which lets a machine where nothing can spawn look identical to a
/// machine where the subject merely timed out.
/// </summary>
public enum ProcessOutcome
{
    Exited,       // Ran to completion; ExitCode is the child's own.
    DidNotStart,  // Never spawned: file not found, not executable.
    TimedOut,     // Spawned, then killed when the timeout elapsed.
    Canceled,     // Spawned, then killed because the caller's token fired.
}

/// <summary>
/// Result of running an external process. ExitCode is -1 for every ending
/// but Exited, so callers that only read it are unaffected; Outcome says
/// which ending it was. Canceled is separate from TimedOut because a
/// pipeline that abandons its probes says nothing about how long the child
/// was going to take.
/// </summary>
public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    System.TimeSpan Duration,
    ProcessOutcome Outcome = ProcessOutcome.Exited);

/// <summary>
/// Runs an external process and returns its result. Production wrapper
/// uses System.Diagnostics.Process; tests use FakeProcessRunner.
/// Ghostty.Core never calls Process.Start directly so the resolver
/// types stay pure-logic and unit-testable on Linux runners.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        System.TimeSpan timeout,
        CancellationToken ct);
}
