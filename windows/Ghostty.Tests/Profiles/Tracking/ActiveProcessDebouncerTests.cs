using Ghostty.Core.Profiles.Tracking;
using Xunit;

namespace Ghostty.Tests.Profiles.Tracking;

public sealed class ActiveProcessDebouncerTests
{
    [Fact]
    public void Observe_FirstValue_EmitsAfterWindow()
    {
        var d = new ActiveProcessDebouncer(windowMs: 250);
        Assert.Null(d.Observe(rootPid: 100, exeBasename: "pwsh.exe", commandLine: null, nowMs: 0));

        Assert.Equal("pwsh.exe", d.Observe(rootPid: 100, exeBasename: "pwsh.exe", commandLine: null, nowMs: 250)!.ExeBasename);
    }

    [Fact]
    public void Observe_ChangeWithinWindow_RestartsTimer()
    {
        var d = new ActiveProcessDebouncer(windowMs: 250);
        Assert.Null(d.Observe(100, "pwsh.exe", null, nowMs: 0));
        // Flip to cmd at 100 ms - restarts the 250 ms window.
        Assert.Null(d.Observe(100, "cmd.exe", null, nowMs: 100));
        // At 200 ms (within new window from 100), still no emit.
        Assert.Null(d.Observe(100, "cmd.exe", null, nowMs: 200));
        // At 350 ms (>= 100 + 250), emit cmd.exe.
        Assert.Equal("cmd.exe", d.Observe(100, "cmd.exe", null, nowMs: 350)!.ExeBasename);
    }

    [Fact]
    public void Observe_FlipBackToOriginal_NoEmit()
    {
        var d = new ActiveProcessDebouncer(windowMs: 250);
        // Bring pwsh up to emitted state at 250 ms.
        Assert.Null(d.Observe(100, "pwsh.exe", null, nowMs: 0));
        Assert.Equal("pwsh.exe", d.Observe(100, "pwsh.exe", null, nowMs: 250)!.ExeBasename);
        // Flip to cmd at 300 ms.
        Assert.Null(d.Observe(100, "cmd.exe", null, nowMs: 300));
        // Flip back to pwsh at 400 ms (within window).
        Assert.Null(d.Observe(100, "pwsh.exe", null, nowMs: 400));
        // At 700 ms with stable pwsh, no emit (pwsh is already the emitted value).
        Assert.Null(d.Observe(100, "pwsh.exe", null, nowMs: 700));
    }

    [Fact]
    public void Observe_MultipleRoots_IndependentTimers()
    {
        var d = new ActiveProcessDebouncer(windowMs: 250);
        Assert.Null(d.Observe(100, "pwsh.exe", null, nowMs: 0));
        Assert.Null(d.Observe(200, "bash.exe", null, nowMs: 100));

        Assert.Equal("pwsh.exe", d.Observe(100, "pwsh.exe", null, nowMs: 250)!.ExeBasename);
        Assert.Equal("bash.exe", d.Observe(200, "bash.exe", null, nowMs: 350)!.ExeBasename);
    }

    [Fact]
    public void Observe_NullToValue_EmitsAfterWindow()
    {
        // Going from "no foreground process" to a value should emit.
        var d = new ActiveProcessDebouncer(windowMs: 250);
        Assert.Null(d.Observe(100, exeBasename: null, commandLine: null, nowMs: 0));
        Assert.Null(d.Observe(100, "vim.exe", null, nowMs: 100));
        Assert.Equal("vim.exe", d.Observe(100, "vim.exe", null, nowMs: 350)!.ExeBasename);
    }

    [Fact]
    public void Observe_ValueToNull_EmitsAfterWindow()
    {
        var d = new ActiveProcessDebouncer(windowMs: 250);
        Assert.Null(d.Observe(100, "vim.exe", null, nowMs: 0));
        Assert.Equal("vim.exe", d.Observe(100, "vim.exe", null, nowMs: 250)!.ExeBasename);
        Assert.Null(d.Observe(100, null, null, nowMs: 300));
        var emitted = d.Observe(100, null, null, nowMs: 550);
        Assert.NotNull(emitted);
        Assert.Null(emitted!.ExeBasename);
    }
}
