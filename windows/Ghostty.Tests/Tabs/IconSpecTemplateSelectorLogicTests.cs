using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Tabs;

public sealed class IconSpecTemplateSelectorLogicTests
{
    [Fact]
    public void Pick_Mdl2Token_ReturnsFontIcon()
    {
        var kind = IconSpecTemplateLogic.PickKind(new IconSpec.Mdl2Token(0xE756));
        Assert.Equal(IconTemplateKind.FontIcon, kind);
    }

    [Fact]
    public void Pick_BrandKey_ReturnsImage()
    {
        var kind = IconSpecTemplateLogic.PickKind(new IconSpec.BrandKey("ubuntu", 16));
        Assert.Equal(IconTemplateKind.Image, kind);
    }

    [Fact]
    public void Pick_BundledKey_ReturnsImage()
    {
        var kind = IconSpecTemplateLogic.PickKind(new IconSpec.BundledKey("pwsh"));
        Assert.Equal(IconTemplateKind.Image, kind);
    }

    [Fact]
    public void Pick_Path_ReturnsImage()
    {
        var kind = IconSpecTemplateLogic.PickKind(new IconSpec.Path(@"C:\foo.png"));
        Assert.Equal(IconTemplateKind.Image, kind);
    }

    [Fact]
    public void Pick_AutoForExe_ReturnsImage()
    {
        var kind = IconSpecTemplateLogic.PickKind(new IconSpec.AutoForExe(@"C:\foo.exe"));
        Assert.Equal(IconTemplateKind.Image, kind);
    }

    [Fact]
    public void Pick_AutoForWslDistro_ReturnsImage()
    {
        var kind = IconSpecTemplateLogic.PickKind(new IconSpec.AutoForWslDistro("Ubuntu"));
        Assert.Equal(IconTemplateKind.Image, kind);
    }
}
