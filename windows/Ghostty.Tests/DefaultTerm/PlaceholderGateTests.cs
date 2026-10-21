using Ghostty.Core.DefaultTerm;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The nil-identity gate and the production capture store: a build with
/// placeholder CLSIDs must refuse every real write (its keys are not
/// edition-ours, and the proxy CLSID is shared with every other
/// terminal), and the production engine must carry a store so disable
/// can restore a prior selection.
/// </summary>
public sealed class PlaceholderGateTests : IDisposable
{
    private readonly string _root;

    public PlaceholderGateTests()
    {
        _root = @"Software\WinttyDeftermPlaceholderGate\"
            + Guid.NewGuid().ToString("N");
    }

    public void Dispose()
    {
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser,
            Microsoft.Win32.RegistryView.Default);
        baseKey.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
    }

    [Fact]
    public void PlaceholderIdentity_RefusesEveryRealWrite()
    {
        var engine = new DefaultTermRegistration(
            DefaultTermRegistryPaths.UnderTestRoot(_root),
            NilIdentity(),
            @"C:\wintty\Wintty.exe",
            @"C:\wintty\OpenConsole.exe",
            @"C:\wintty\OpenConsoleProxy.dll");

        var result = engine.Enable();

        Assert.Equal(EnableStatus.Failed, result.Status);
        Assert.NotNull(result.FailedKey);
        Assert.Contains("identity", result.FailedKey, StringComparison.OrdinalIgnoreCase);

        // Nothing was written anywhere under the root: not the CLSID
        // tree, not the shared proxy key, not the selection.
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser,
            Microsoft.Win32.RegistryView.Default);
        using var rootKey = baseKey.OpenSubKey(_root);
        Assert.Null(rootKey);

        Assert.Equal(DisableResult.NotRegistered, engine.Disable());
    }

    [Fact]
    public void ProductionEngine_CarriesACaptureStore()
    {
        // The default store path is derived, not null, and lives under
        // the user's local app data, not the install directory.
        var path = DefaultTermBootstrap.DefaultStorePath;
        Assert.NotNull(path);
        Assert.Contains(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("defterm-state.json", path, StringComparison.OrdinalIgnoreCase);
    }

    private static IDefaultTermIdentityView NilIdentity() => new TestIdentity(
        "{00000000-0000-0000-0000-000000000000}",
        "{00000000-0000-0000-0000-000000000000}",
        "{3171DE52-6EFA-4AEF-8A9F-D02BD67E7A4F}",
        new[] { "{6F23DA90-15C5-4203-9DB0-64E73F1B1B00}" });

    private sealed class TestIdentity : IDefaultTermIdentityView
    {
        public TestIdentity(string terminalClsid, string consoleClsid, string proxyClsid, string[] handoffIids)
        {
            TerminalClsid = terminalClsid;
            ConsoleClsid = consoleClsid;
            ProxyClsid = proxyClsid;
            HandoffIids = handoffIids;
        }

        public string TerminalClsid { get; }

        public string ConsoleClsid { get; }

        public string ProxyClsid { get; }

        public string[] HandoffIids { get; }
    }
}
