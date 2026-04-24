using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;

namespace Ghostty.Tests.Profiles.Fakes;

/// <summary>
/// Returns canned ProcessResult per (fileName, args-joined-with-space).
/// Unmatched calls return ExitCode=-1 stdout/stderr empty (mirrors the
/// "process did not start" contract). Records every call for assertion.
/// </summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessResult> _canned = new();
    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();

    public void EnqueueResult(string fileName, IEnumerable<string> args, ProcessResult result)
    {
        _canned[Key(fileName, args)] = result;
    }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Calls.Add((fileName, args));
        var key = Key(fileName, args);
        if (_canned.TryGetValue(key, out var result))
            return Task.FromResult(result);
        return Task.FromResult(new ProcessResult(-1, "", "", TimeSpan.Zero));
    }

    private static string Key(string fileName, IEnumerable<string> args)
        => $"{fileName} {string.Join(' ', args)}";
}
