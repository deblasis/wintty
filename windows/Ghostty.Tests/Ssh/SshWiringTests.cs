using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Config;
using Ghostty.Core.Panes;
using Xunit;

namespace Ghostty.Tests.Ssh;

public class SshMenuAndConfigTests
{
    [Fact]
    public void Menu_NoOffer_HasNoDownloadEntry()
    {
        var items = PaneContextMenuModel.Build(hasSelection: false, isZoomed: false);
        Assert.DoesNotContain(items, i => i.Command == PaneMenuCommand.SftpDownload);
    }

    [Fact]
    public void Menu_Offer_AddsDownloadAfterSelectAll()
    {
        var items = PaneContextMenuModel.Build(hasSelection: false, isZoomed: false, sftpDownloadName: "notes.md");
        var index = items.ToList().FindIndex(i => i.Command == PaneMenuCommand.SftpDownload);
        Assert.Equal(3, index);
        Assert.Equal(PaneMenuCommand.SelectAll, items[index - 1].Command);
        Assert.Equal("Download \"notes.md\" via SFTP...", items[index].Label);
        Assert.True(items[index].IsEnabled);
        // The rest of the menu is unchanged around it.
        Assert.Equal(
            PaneContextMenuModel.Build(false, false).Count + 1,
            items.Count);
    }

    [Fact]
    public void Menu_LongNameIsShortened()
    {
        var name = new string('a', 80) + ".log";
        var item = PaneContextMenuModel.Build(false, false, name)
            .Single(i => i.Command == PaneMenuCommand.SftpDownload);
        Assert.True(item.Label.Length < 70);
        Assert.Contains("...", item.Label);
        Assert.EndsWith(".log\" via SFTP...", item.Label);
    }

    [Fact]
    public void ConfigView_CarriesConnectionsAndTheirWarnings()
    {
        var pairs = new Dictionary<string, List<string>>
        {
            ["ssh.devel.host"] = ["devel.local"],
            ["ssh.devel.user"] = ["jgill"],
            ["ssh.broken.user"] = ["nobody"],
            ["profile.p.name"] = ["P"],
            ["profile.p.command"] = ["cmd.exe"],
        };
        var view = ConfigServiceProfileParser.ParseAll(pairs, _ => null);

        var connection = Assert.Single(view.SshConnections);
        Assert.Equal("devel", connection.Id);
        Assert.Single(view.ParsedProfiles);
        Assert.Contains(view.ProfileWarnings, w => w.StartsWith("ssh 'broken':"));
    }

    [Fact]
    public void ConfigView_NoSshKeys_NoConnections()
    {
        var view = ConfigServiceProfileParser.ParseAll(new Dictionary<string, List<string>>(), _ => null);
        Assert.Empty(view.SshConnections);
    }

    [Theory]
    [InlineData("ssh.devel.host", true)]
    [InlineData("ssh.devel.identity-file", true)]
    [InlineData("ssh-hosts-user", false)]
    [InlineData("profile.x.name", false)]
    public void ConnectionKeysAreAbsorbedByTheDiagnosticFilter(string key, bool expected)
        => Assert.Equal(expected, WindowsOnlyKeys.IsSshConnectionKey(key));
}
