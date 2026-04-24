using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class ProfileDefTests
{
    [Fact]
    public void ProfileDef_AllOptionalNull_RecordEquality()
    {
        var a = new ProfileDef(Id: "pwsh", Name: "PowerShell", Command: "pwsh.exe");
        var b = new ProfileDef(Id: "pwsh", Name: "PowerShell", Command: "pwsh.exe");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ProfileDef_DifferentId_RecordInequality()
    {
        var a = new ProfileDef(Id: "pwsh", Name: "PowerShell", Command: "pwsh.exe");
        var b = new ProfileDef(Id: "cmd", Name: "PowerShell", Command: "pwsh.exe");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ProfileDef_VisualOverridesDefaultsEmpty()
    {
        var a = new ProfileDef(Id: "pwsh", Name: "PowerShell", Command: "pwsh.exe");
        Assert.NotNull(a.Visuals);
        Assert.Null(a.Visuals.Theme);
        Assert.Null(a.Visuals.BackgroundOpacity);
    }
}
