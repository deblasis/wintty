using System.Runtime.Versioning;
using Ghostty.Core.JumpList;
using Windows.Win32.UI.Shell;
using Xunit;

namespace Ghostty.Tests.JumpList;

// Propagate ShellLinkTitleHelper's platform gate instead of suppressing
// CA1416: the real CLSID_ShellLink COM object it exercises only exists
// on Windows.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ShellLinkTitleHelperTests
{
    [Fact]
    public void SetTitle_OnRealShellLink_DoesNotThrowAndTheTitleSticks()
    {
        // Regression test: SetTitle used to unconditionally throw
        // InvalidCastException (E_NOINTERFACE) trying to reach the
        // shell link's IPropertyStore facet through a fabricated
        // COM-callable wrapper, which aborted the whole jump list
        // build on every launch. IShellLinkW is created here exactly
        // as CustomDestinationListFacade.CreateShellLink does: via the
        // ShellLink coclass, not a fake or a mock, so the real
        // cross-interface QueryInterface path runs.
        var link = ShellLink.CreateInstance<IShellLinkW>();

        var thrown = Record.Exception(() => ShellLinkTitleHelper.SetTitle(link, "Wintty Title Helper Test"));
        Assert.Null(thrown);

        // Confirm the title was actually committed to the property
        // store, not merely that the call returned - a fix that
        // swallows the failure without setting anything would pass
        // the assertion above but fail this one.
        Assert.Equal("Wintty Title Helper Test", ShellLinkTitleHelper.GetTitleForTests(link));
    }
}
