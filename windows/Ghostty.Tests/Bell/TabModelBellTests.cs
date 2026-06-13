using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Ghostty.Tests.Tabs;
using Xunit;

namespace Ghostty.Tests.Bell;

public class TabModelBellTests
{
    [Fact]
    public void BellRinging_DefaultsFalse()
    {
        var tab = new TabModel(new FakePaneHost());
        Assert.False(tab.BellRinging);
    }

    [Fact]
    public void BellRinging_Set_RaisesPropertyChanged()
    {
        var tab = new TabModel(new FakePaneHost());
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.BellRinging = true;

        Assert.True(tab.BellRinging);
        Assert.Contains(nameof(TabModel.BellRinging), raised);
    }

    [Fact]
    public void BellRinging_SetSameValue_DoesNotRaise()
    {
        var tab = new TabModel(new FakePaneHost());
        var count = 0;
        tab.PropertyChanged += (_, _) => count++;

        tab.BellRinging = false; // already false

        Assert.Equal(0, count);
    }
}
