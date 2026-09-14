using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Ghostty.Core.JumpList;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Xunit;

namespace Ghostty.Tests.JumpList;

// ShellLinkCollection is [SupportedOSPlatform("windows6.1")]; this class
// declares a compatible (narrower) gate so CA1416 is satisfied rather
// than suppressed.
//
// Real shell COM objects throughout (EnumerableObjectCollection,
// ShellLink), all in memory: no ICustomDestinationList, no
// AppUserModelID, nothing committed to any real jump list.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ShellLinkCollectionTests
{
    private const string Exe = @"C:\Program Files\Wintty\wintty.exe";

    [Fact]
    public void Build_WithOneRealShellLink_ReturnsAnIObjectArrayThatCountsIt()
    {
        // Regression test: the collection used to reach its IObjectArray
        // facet by round-tripping it through a COM-callable wrapper and
        // re-wrapping it, which threw E_NOINTERFACE, so Commit never ran
        // and the jump list was never committed. GetCount is the first
        // call ICustomDestinationList would make on the array.
        var array = ShellLinkCollection.Build(
            new[] { (Exe, "--jumplist-action=new-window", "New Window") },
            (_, _, _, _) => Assert.Fail("no entry should be skipped"));

        array.GetCount(out var count);
        Assert.Equal(1u, count);
    }

    [Fact]
    public void Build_SkipsOneFailingEntry_ReportsItsArgumentsAndTitle_AndKeepsTheRest()
    {
        var failure = new InvalidOperationException("shell rejected the entry");
        var skipped = new List<(Exception error, string exePath, string args, string title)>();

        var array = ShellLinkCollection.Build(
            new[]
            {
                (Exe, "--jumplist-profile=broken", "Broken Profile"),
                (Exe, "--jumplist-profile=pwsh", "PowerShell"),
            },
            (exe, args, title) => args.EndsWith("broken", StringComparison.Ordinal)
                ? throw failure
                : ShellLinkCollection.CreateShellLink(exe, args, title),
            (error, exe, args, title) => skipped.Add((error, exe, args, title)));

        array.GetCount(out var count);
        Assert.Equal(1u, count);

        var only = Assert.Single(skipped);
        Assert.Same(failure, only.error);
        Assert.Equal(Exe, only.exePath);
        Assert.Equal("--jumplist-profile=broken", only.args);
        Assert.Equal("Broken Profile", only.title);
    }

    [Fact]
    public void Build_WhenEveryEntryFails_ThrowsOnce_InsteadOfReturningAnEmptyList()
    {
        // An empty collection handed to AddUserTasks/AppendCategory and
        // committed would replace a good jump list with an empty one. The
        // whole build must abort with one failure instead, and no
        // per-entry skip lines.
        var first = new InvalidOperationException("first");
        var skipped = 0;

        var thrown = Assert.Throws<InvalidOperationException>(() => ShellLinkCollection.Build(
            new[]
            {
                (Exe, "--jumplist-action=new-window", "New Window"),
                (Exe, "--jumplist-action=new-tab", "New Tab in Current Window"),
            },
            (_, args, _) => throw (args.EndsWith("new-window", StringComparison.Ordinal)
                ? first
                : new InvalidOperationException("second")),
            (_, _, _, _) => skipped++));

        Assert.Same(first, thrown.InnerException);
        Assert.Contains("None of the 2", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("\"New Window\"", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("--jumplist-action=new-window", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Build_WithNoEntries_ReturnsAnEmptyArrayWithoutThrowing()
    {
        var array = ShellLinkCollection.Build(
            Array.Empty<(string, string, string)>(),
            (_, _, _, _) => Assert.Fail("nothing to skip"));

        array.GetCount(out var count);
        Assert.Equal(0u, count);
    }
}
