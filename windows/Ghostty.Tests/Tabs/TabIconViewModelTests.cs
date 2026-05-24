using System.Collections.Generic;
using System.ComponentModel;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public sealed class TabIconViewModelTests
{
    [Fact]
    public void Construct_FromBrandKey_ExposesSpecAndTooltip()
    {
        var vm = new TabIconViewModel(new IconSpec.BrandKey("ubuntu", 16), "Ubuntu (WSL)");
        Assert.IsType<IconSpec.BrandKey>(vm.Icon);
        Assert.Equal("Ubuntu (WSL)", vm.TooltipText);
    }

    [Fact]
    public void Construct_FromMdl2Token_FlagsIsMdl2Glyph()
    {
        var vm = new TabIconViewModel(new IconSpec.Mdl2Token(0xE756), "PowerShell");
        Assert.True(vm.IsMdl2Glyph);
        Assert.Equal(0xE756, vm.Mdl2CodePoint);
    }

    [Fact]
    public void Construct_FromBrandKey_IsNotMdl2Glyph()
    {
        var vm = new TabIconViewModel(new IconSpec.BrandKey("ubuntu", 16), "Ubuntu");
        Assert.False(vm.IsMdl2Glyph);
        Assert.Equal(0, vm.Mdl2CodePoint);
    }

    [Fact]
    public void SetIcon_RaisesPropertyChangedForIconAndTooltip()
    {
        var vm = new TabIconViewModel(new IconSpec.BundledKey("pwsh"), "PowerShell");
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SetIcon(new IconSpec.BrandKey("cmd", 16), "Command Prompt");

        Assert.Contains(nameof(TabIconViewModel.Icon), raised);
        Assert.Contains(nameof(TabIconViewModel.TooltipText), raised);
        Assert.Equal("Command Prompt", vm.TooltipText);
    }

    [Fact]
    public void SetIcon_FlippingKind_RaisesPropertyChangedForDerivedFlags()
    {
        var vm = new TabIconViewModel(new IconSpec.BrandKey("ubuntu", 16), "Ubuntu");
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SetIcon(new IconSpec.Mdl2Token(0xE756), "PowerShell");

        Assert.Contains(nameof(TabIconViewModel.IsMdl2Glyph), raised);
        Assert.Contains(nameof(TabIconViewModel.Mdl2CodePoint), raised);
        Assert.True(vm.IsMdl2Glyph);
        Assert.Equal(0xE756, vm.Mdl2CodePoint);
    }
}
