using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class ProfileOrderResolverIconFallbackTests
{
    [Fact]
    public void Resolve_ProfileWithExplicitIcon_KeepsIt()
    {
        var def = MakeDef(command: "pwsh.exe", icon: new IconSpec.BrandKey("ubuntu", null));
        var resolved = ResolveSingle(def);
        var brand = Assert.IsType<IconSpec.BrandKey>(resolved.Icon);
        Assert.Equal("ubuntu", brand.Key);
    }

    [Fact]
    public void Resolve_NoIcon_PwshCommand_FallsBackToPwshBrand()
    {
        var def = MakeDef(command: "pwsh.exe", icon: null);
        var resolved = ResolveSingle(def);
        var brand = Assert.IsType<IconSpec.BrandKey>(resolved.Icon);
        Assert.Equal("pwsh", brand.Key);
    }

    [Fact]
    public void Resolve_NoIcon_QuotedAbsolutePwshPath_FallsBackToPwshBrand()
    {
        var def = MakeDef(command: "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\"", icon: null);
        var resolved = ResolveSingle(def);
        var brand = Assert.IsType<IconSpec.BrandKey>(resolved.Icon);
        Assert.Equal("pwsh", brand.Key);
    }

    [Fact]
    public void Resolve_NoIcon_CmdCommand_FallsBackToCmdBrand()
    {
        var def = MakeDef(command: "cmd.exe", icon: null);
        var resolved = ResolveSingle(def);
        var brand = Assert.IsType<IconSpec.BrandKey>(resolved.Icon);
        Assert.Equal("cmd", brand.Key);
    }

    [Fact]
    public void Resolve_NoIcon_UnknownCommand_FallsBackToDefault()
    {
        var def = MakeDef(command: "totally-bespoke-shell.exe", icon: null);
        var resolved = ResolveSingle(def);
        var bundled = Assert.IsType<IconSpec.BundledKey>(resolved.Icon);
        Assert.Equal("default", bundled.Key);
    }

    [Fact]
    public void Resolve_NoIcon_EmptyCommand_FallsBackToDefault()
    {
        var def = MakeDef(command: "", icon: null);
        var resolved = ResolveSingle(def);
        var bundled = Assert.IsType<IconSpec.BundledKey>(resolved.Icon);
        Assert.Equal("default", bundled.Key);
    }

    private static ProfileDef MakeDef(string command, IconSpec? icon) =>
        new(Id: "test", Name: "Test", Command: command, Icon: icon);

    private static ResolvedProfile ResolveSingle(ProfileDef def)
    {
        var set = ProfileOrderResolver.Resolve(
            user: new[] { def },
            discovered: System.Array.Empty<DiscoveredProfile>(),
            profileOrder: null,
            defaultProfileId: null,
            hiddenIds: new HashSet<string>());
        return set.Visible.Single();
    }
}
