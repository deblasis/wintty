using System.Collections.Generic;
using Ghostty.Core.Env;
using Xunit;

namespace Ghostty.Tests.Env;

public class NoColorStartupTests
{
    /// <summary>Records the side effects the app layer would perform.</summary>
    private sealed class Recorder
    {
        public int Removes;
        public readonly List<string> Persisted = new();
        public void Remove() => Removes++;
        public void Persist(string mode) => Persisted.Add(mode);
    }

    [Fact]
    public void Absent_does_nothing()
    {
        var r = new Recorder();
        var notice = NoColorStartup.Resolve(present: false, NoColorPolicy.Notify, r.Remove, r.Persist);
        Assert.Null(notice);
        Assert.Equal(0, r.Removes);
        Assert.Empty(r.Persisted);
    }

    [Fact]
    public void Keep_honors_silently()
    {
        var r = new Recorder();
        var notice = NoColorStartup.Resolve(present: true, NoColorPolicy.Keep, r.Remove, r.Persist);
        Assert.Null(notice);
        Assert.Equal(0, r.Removes); // honored, not stripped
        Assert.Empty(r.Persisted);
    }

    [Fact]
    public void Strip_removes_env_without_a_notice()
    {
        var r = new Recorder();
        var notice = NoColorStartup.Resolve(present: true, NoColorPolicy.Strip, r.Remove, r.Persist);
        Assert.Null(notice);
        Assert.Equal(1, r.Removes);
        Assert.Empty(r.Persisted);
    }

    [Fact]
    public void Notify_honors_and_returns_notice_without_stripping()
    {
        var r = new Recorder();
        var notice = NoColorStartup.Resolve(present: true, NoColorPolicy.Notify, r.Remove, r.Persist);
        Assert.NotNull(notice);
        Assert.Equal(0, r.Removes); // honored at startup, not stripped
        Assert.Equal("no-color", notice!.DedupKey);
        Assert.Equal(2, notice.Actions.Count);
    }

    [Fact]
    public void Unrecognized_override_falls_through_to_notify()
    {
        var r = new Recorder();
        var notice = NoColorStartup.Resolve(present: true, "bogus", r.Remove, r.Persist);
        Assert.NotNull(notice);
        Assert.Equal(0, r.Removes);
    }

    [Fact]
    public void EnableColor_action_strips_and_persists_strip()
    {
        var r = new Recorder();
        var notice = NoColorStartup.Resolve(present: true, NoColorPolicy.Notify, r.Remove, r.Persist);

        notice!.Actions[0].Invoke(); // "Enable color" (primary)

        Assert.True(notice.Actions[0].IsPrimary);
        Assert.Equal(1, r.Removes);
        Assert.Equal(new[] { NoColorPolicy.Strip }, r.Persisted);
    }

    [Fact]
    public void KeepItOff_action_persists_keep_without_stripping()
    {
        var r = new Recorder();
        var notice = NoColorStartup.Resolve(present: true, NoColorPolicy.Notify, r.Remove, r.Persist);

        notice!.Actions[1].Invoke(); // "Keep it off"

        Assert.Equal(0, r.Removes);
        Assert.Equal(new[] { NoColorPolicy.Keep }, r.Persisted);
    }
}
