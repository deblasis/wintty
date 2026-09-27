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

        // Both ends of the call pinned: the file being opened is the one
        // the rotation moved aside, and the disposition is the non-truncating one.
        Assert.Equal("GpuLogPath", open.Arg(0));
        Assert.Equal("OPEN_ALWAYS", open.Arg(4));

        // The spelling alone is not the guarantee - the VALUE is. A const
        // drifted to 2 (CREATE_ALWAYS) with the name unchanged passes the
        // argument assert above on every green run. (.Value, not the whole
        // initializer: that renders as "= 4".)
        var field = ShellSource.Load("Program.cs").Field("OPEN_ALWAYS");
        Assert.Equal("4", field.Variable.Initializer?.Value.ToString());
    }

    [Fact]
    public void The_banner_is_seeded_before_anything_can_crash()
    {
        // ReportFatal reads a cached banner string. On the plain CLI path
        // (no WINTTY_GPU_LOG) nothing else caches it, so the cache has to
        // be filled on MainImpl's own frame: after the resolver is in
        // place (the FFI read resolves through it), before any action,
        // redirect or libghostty init - any of which can be the crash the
        // entry then describes.
        var program = ShellSource.Load("Program.cs");
        var mainImpl = program.Method("MainImpl");

        var seed = Assert.Single(mainImpl.Calls("VersionBanner.Header"));

        var register = mainImpl.Call("RegisterNativeResolver");
        Assert.True(
            seed.SpanStart > register.SpanStart,
            "the seed's FFI read only resolves once the native resolver is registered");

        var firstCrashCapable = new[] { "RedirectStderrToFile", "InitGhostty", "CliRunAction", "TrySendListThemesMessage" }
            .SelectMany(target => mainImpl.Calls(target))
            .Select(call => call.SpanStart)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        Assert.True(
            seed.SpanStart < firstCrashCapable,
            "the banner must be cached before any action, redirect or init that could crash first");
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

    [Fact]
    public void The_gui_crash_log_carries_the_version_banner()
    {
        // App.LogUnhandled's %LOCALAPPDATA% crash.log is the artifact the
        // GUI's own handlers write and the one a Windows user pastes most
        // often; the startup crash log getting the banner and not this one
        // would leave the most-pasted file the only anonymous one.
        var app = ShellSource.Load("App.xaml.cs");
        var logUnhandled = app.Method("LogUnhandled");

        var banner = Assert.Single(logUnhandled.Calls("VersionBanner.Header"));
        var append = logUnhandled.Call("File.AppendAllText");
        // Containment, not ordering: the banner is an argument of the
        // append call itself, so its span starts AFTER the call's name. A
        // banner that moved outside the written entry fails the containment.
        Assert.True(
            append.Span.Contains(banner.Span),
            "the banner is part of the entry the crash-log append writes");
    }
}
