using Ghostty.Core.Env;
using Xunit;

namespace Ghostty.Tests.Env;

public class NoColorPolicyTests
{
    [Fact]
    public void Absent_never_strips_or_notifies()
    {
        // NO_COLOR not in the environment: nothing to do regardless of mode.
        foreach (var mode in NoColorPolicy.Allowed)
        {
            var o = NoColorPolicy.Decide(present: false, mode);
            Assert.False(o.Strip);
            Assert.False(o.Notify);
        }
    }

    [Fact]
    public void Notify_default_honors_no_color_and_notifies()
    {
        // Default honors NO_COLOR (does NOT strip — a terminal passes the
        // standard through) but informs the user, offering to enable color.
        var o = NoColorPolicy.Decide(present: true, NoColorPolicy.Notify);
        Assert.False(o.Strip);
        Assert.True(o.Notify);
    }

    [Fact]
    public void Strip_mode_strips_silently()
    {
        var o = NoColorPolicy.Decide(present: true, NoColorPolicy.Strip);
        Assert.True(o.Strip);
        Assert.False(o.Notify);
    }

    [Fact]
    public void Keep_mode_honors_no_color_silently()
    {
        var o = NoColorPolicy.Decide(present: true, NoColorPolicy.Keep);
        Assert.False(o.Strip);
        Assert.False(o.Notify);
    }

    [Fact]
    public void Unknown_mode_falls_back_to_notify()
    {
        // ParseStringAllowed already normalizes to Default before this is
        // called, but Decide must be self-consistent for any string.
        var o = NoColorPolicy.Decide(present: true, "banana");
        Assert.False(o.Strip);
        Assert.True(o.Notify);
    }

    [Fact]
    public void Default_is_notify_and_is_an_allowed_value()
    {
        Assert.Equal(NoColorPolicy.Notify, NoColorPolicy.Default);
        Assert.Contains(NoColorPolicy.Default, NoColorPolicy.Allowed);
    }
}
