using Ghostty.Core.Hosting;
using Ghostty.Core.Session;
using Xunit;

namespace Ghostty.Tests.Session;

public class SessionGateTests
{
    [Theory]
    [InlineData(WindowSaveState.Never, true, false)]
    [InlineData(WindowSaveState.Never, false, false)]
    [InlineData(WindowSaveState.Always, true, true)]
    [InlineData(WindowSaveState.Always, false, true)]
    [InlineData(WindowSaveState.Default, true, true)]
    [InlineData(WindowSaveState.Default, false, false)]
    public void ShouldRestore(WindowSaveState state, bool cleanShutdown, bool expected)
    {
        Assert.Equal(expected, SessionGate.ShouldRestore(state, cleanShutdown));
    }

    [Fact]
    public void ShouldPersist_FalseOnlyForNever()
    {
        Assert.False(SessionGate.ShouldPersist(WindowSaveState.Never));
        Assert.True(SessionGate.ShouldPersist(WindowSaveState.Default));
        Assert.True(SessionGate.ShouldPersist(WindowSaveState.Always));
    }
}
