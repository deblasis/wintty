using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That the artifacts Program.cs writes name their own build (#968).
///
/// The rolling log's header record is behaviour-tested in
/// LoggingBootstrapTests; what only source can see are the Program.cs call
/// sites, because the shell project cannot be loaded into a test host.
/// Deleting any of these calls still compiles, still exits cleanly, and
/// the artifact goes back to opening with timestamps only - the silence
/// these guards keep loud.
/// </summary>
public class ArtifactIdentityWiringTests
{
    [Fact]
    public void Gpu_log_rotation_runs_before_the_log_is_opened()
    {
        var redirect = ShellSource.Load("Program.cs").Method("RedirectStderrToFile");

        // The argument pinned inside the call, not the callee alone: a
        // rotate of anything but the path about to be opened is a no-op
        // with a witness.
        var rotate = Assert.Single(redirect.Calls("GpuLogRotation.Rotate"));
        Assert.Equal("GpuLogPath", rotate.Arg(0));

        var open = Assert.Single(redirect.Calls("CreateFileW"));
        Assert.True(
            rotate.SpanStart < open.SpanStart,
            "the rotation must move the previous launch's log aside before the open");
    }

    [Fact]
    public void Gpu_log_opens_without_truncating()
    {
        // The half of #968 the rotation does not cover: a failed rotation
        // (the old log held by a tailer) must degrade to appending, never
        // to destroying. CREATE_ALWAYS is the per-launch truncation the
        // issue is about; this pins the disposition argument of the one
        // CreateFileW in the file, so the truncation cannot come back
        // behind a rotation that only usually works.
        var redirect = ShellSource.Load("Program.cs").Method("RedirectStderrToFile");
        var open = Assert.Single(redirect.Calls("CreateFileW"));
        Assert.Equal("OPEN_ALWAYS", open.Arg(4));
    }

    [Fact]
    public void Gpu_log_carries_the_version_banner()
    {
        var redirect = ShellSource.Load("Program.cs").Method("RedirectStderrToFile");

        // After the redirect is installed, or the banner went to the
        // terminal and the file still opens with timestamps only.
        var setError = redirect.Call("Console.SetError");
        var banner = Assert.Single(redirect.Calls("VersionBanner.Header"));
        Assert.True(
            banner.SpanStart > setError.SpanStart,
            "the banner must be written once Console.Error points at gpu.log");
    }

    [Fact]
    public void The_crash_entry_carries_the_version_banner()
    {
        var program = ShellSource.Load("Program.cs");
        var report = program.Method("ReportFatal");

        var banner = Assert.Single(report.Calls("VersionBanner.Header"));

        // Before BOTH append calls: the banner is part of the entry string
        // the appends write, so it has to be built first - a banner added
        // after the primary append is a banner nothing writes.
        var appends = report.Calls("TryAppendCrashLog");
        Assert.True(appends.Count >= 1, "expected crash-log append calls in ReportFatal");
        Assert.True(
            banner.SpanStart < System.Linq.Enumerable.Min(appends, a => a.SpanStart),
            "the banner is part of the entry the crash-log appends write");
    }
}
