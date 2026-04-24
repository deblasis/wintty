using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Result of running an external process. ExitCode is -1 if the
/// process did not start (e.g. file not found).
/// </summary>
public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    System.TimeSpan Duration);

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
