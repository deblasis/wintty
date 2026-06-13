using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabManagerBellTests
{
    private static (TabManager mgr, List<FakePaneHost> hosts) NewManager()
    {
        var hosts = new List<FakePaneHost>();
        var mgr = new TabManager(_ =>
        {
            var h = new FakePaneHost();
            hosts.Add(h);
            return h;
        });
        return (mgr, hosts);
    }

    [Fact]
    public void PaneHost_bell_is_forwarded_to_manager()
    {
        var (mgr, hosts) = NewManager();
        int count = 0;
        mgr.BellRang += (_, _) => count++;

        hosts[0].RaiseBellRang();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Closed_tab_bell_is_not_forwarded()
    {
        var (mgr, hosts) = NewManager();
        mgr.NewTab();              // hosts[1]
        int count = 0;
        mgr.BellRang += (_, _) => count++;

        mgr.CloseTab(mgr.Tabs[1]); // unwires hosts[1]
        hosts[1].RaiseBellRang();

        Assert.Equal(0, count);
    }
}
