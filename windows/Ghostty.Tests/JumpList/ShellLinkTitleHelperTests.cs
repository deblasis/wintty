using System;
using System.Runtime.Versioning;
using Ghostty.Core.JumpList;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.PropertiesSystem;
using Xunit;

namespace Ghostty.Tests.JumpList;

// ShellLinkTitleHelper is [SupportedOSPlatform("windows6.1")]: the real
// CLSID_ShellLink COM object it drives only exists on Windows. This class
// declares a compatible (narrower) gate so CA1416 is satisfied rather
// than suppressed.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class ShellLinkTitleHelperTests
{
    // System.Title: FMTID_SummaryInformation, PIDSI_TITLE. Spelled out
    // here, independently of the helper's own copy, so a wrong key in the
    // helper cannot also be the key this test reads back with.
    private static readonly PROPERTYKEY s_pkeyTitle = new()
    {
        fmtid = new Guid("f29f85e0-4ff9-1068-ab91-08002b27b3d9"),
        pid = 2,
    };

    [Fact]
    public void SetTitle_OnRealShellLink_DoesNotThrowAndTheTitleSticks()
    {
        // Regression test: SetTitle used to unconditionally throw
        // InvalidCastException (E_NOINTERFACE) trying to reach the
        // shell link's IPropertyStore facet through a fabricated
        // COM-callable wrapper, which aborted the whole jump list
        // build on every launch. IShellLinkW is created here exactly
        // as ShellLinkCollection.CreateShellLink does: via the
        // ShellLink coclass, not a fake or a mock, so the real
        // cross-interface QueryInterface path runs.
        var link = ShellLink.CreateInstance<IShellLinkW>();

        var thrown = Record.Exception(() => ShellLinkTitleHelper.SetTitle(link, "Wintty Title Helper Test"));
        Assert.Null(thrown);

        // Confirm the title was actually committed to the property
        // store, not merely that the call returned - a fix that
        // swallows the failure without setting anything would pass
        // the assertion above but fail this one.
        Assert.Equal("Wintty Title Helper Test", ReadTitle(link));
    }

    private static string? ReadTitle(IShellLinkW link)
    {
        var store = (IPropertyStore)link;
        store.GetValue(in s_pkeyTitle, out var pv);
        try
        {
            return pv.Anonymous.Anonymous.Anonymous.pwszVal.ToString();
        }
        finally
        {
            DWritePInvoke.PropVariantClear(ref pv);
        }
    }
}
