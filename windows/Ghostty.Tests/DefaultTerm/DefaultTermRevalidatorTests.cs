using System.Runtime.Versioning;
using Ghostty.Core.DefaultTerm;
using Microsoft.Win32;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// Startup revalidation against a throwaway root: an enabled build checks
/// at every startup that it still owns what it wrote, repairs what a moved
/// install broke (only when this exe is the bound one), unwrites shadows a
/// lost selection left behind, and self-disables when an enable
/// precondition no longer holds. A non-installed exe never repairs: it
/// reports, and the repair waits for the installed build.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DefaultTermRevalidatorTests
{
    private const string WinttyExe = @"C:\Program Files\Wintty\Wintty.exe";
    private const string OpenConsoleExe = @"C:\Program Files\Wintty\bin\openconsole.exe";
    private const string ProxyDll = @"C:\Program Files\Wintty\bin\winttyproxy.dll";
    private const string MovedOpenConsoleExe = @"C:\Program Files\Wintty2\bin\openconsole.exe";

    private const string TestRootsParent = @"Software\Classes\WinttyDefTermTests";

    [Fact]
    public void LostSelection_UnwritesLingeringGroupOnly()
    {
        var root = MintedRoot();
        try
        {
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, a, OpenConsoleExe);
            Assert.Equal(EnableResult.Registered, engine.Enable());

            // The selection moves to a third party (OS dropdown): the
            // console binding this build wrote lingers as a shadow and is
            // unwritten; the third-party selection is untouched.
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationConsoleValue, MintedGuid());
                SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationTerminalValue, MintedGuid());
            }

            var report = Revalidate(NewInputs(engine, paths, a, installedExe: true));

            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            Assert.Null(check.OpenSubKey(paths.ClsidSubkeyOf(a.ConsoleClsid)));
            Assert.Null(check.OpenSubKey(paths.ClsidSubkeyOf(a.ProxyClsid)));
            using (var startup = check.OpenSubKey(paths.StartupSubkey))
            {
                Assert.NotNull(startup);
                Assert.NotNull(startup!.GetValue(DefaultTermRegistration.DelegationTerminalValue));
            }

            Assert.Equal(RevalidationAction.ShadowUnwritten, report.Action);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void LostSelectionWithForeignConsoleKey_TouchesNothing()
    {
        var root = MintedRoot();
        try
        {
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, a, OpenConsoleExe);
            Assert.Equal(EnableResult.Registered, engine.Enable());

            // The selection moved AND another build took the console
            // binding: nothing this build still owns in the group.
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationConsoleValue, MintedGuid());
                SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationTerminalValue, MintedGuid());
                using var server = hkcu.OpenSubKey(
                    paths.ClsidSubkeyOf(a.ConsoleClsid) + @"\LocalServer32", writable: true);
                Assert.NotNull(server);
                server!.SetValue(null, Quote(@"C:\other\build\openconsole.exe"));
            }

            var report = Revalidate(NewInputs(engine, paths, a, installedExe: true));

            Assert.Equal(RevalidationAction.NothingToHold, report.Action);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void InstallMoved_InstalledExe_RewritesBindings()
    {
        var root = MintedRoot();
        try
        {
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);

            // The enabled state names the old install location; this exe
            // IS the bound one (it resolves to the bound path), so the
            // bindings are repaired to the new location.
            var engine = NewEngine(paths, a, MovedOpenConsoleExe);
            Assert.Equal(EnableResult.Registered, engine.Enable());
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                using var server = hkcu.OpenSubKey(
                    paths.ClsidSubkeyOf(a.ConsoleClsid) + @"\LocalServer32", writable: true);
                server!.SetValue(null, Quote(@"C:\old\install\bin\openconsole.exe"));
            }

            var report = Revalidate(NewInputs(engine, paths, a, installedExe: true));

            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var repaired = check.OpenSubKey(paths.ClsidSubkeyOf(a.ConsoleClsid) + @"\LocalServer32");
            Assert.NotNull(repaired);
            Assert.Equal(Quote(MovedOpenConsoleExe), (string?)repaired!.GetValue(null));
            Assert.Equal(RevalidationAction.BindingsRepaired, report.Action);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void InstallMoved_DevExe_OnlyReports()
    {
        var root = MintedRoot();
        try
        {
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, a, OpenConsoleExe);
            Assert.Equal(EnableResult.Registered, engine.Enable());
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                using var server = hkcu.OpenSubKey(
                    paths.ClsidSubkeyOf(a.ConsoleClsid) + @"\LocalServer32", writable: true);
                server!.SetValue(null, Quote(@"C:\old\install\bin\openconsole.exe"));
            }

            var report = Revalidate(NewInputs(engine, paths, a, installedExe: false));

            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var untouched = check.OpenSubKey(paths.ClsidSubkeyOf(a.ConsoleClsid) + @"\LocalServer32");
            Assert.NotNull(untouched);
            Assert.Equal(Quote(@"C:\old\install\bin\openconsole.exe"), (string?)untouched!.GetValue(null));
            Assert.Equal(RevalidationAction.DriftReported, report.Action);
            Assert.Contains("bound", report.Notice, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void SingleInstanceOff_SelfDisables()
    {
        var root = MintedRoot();
        try
        {
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, a, OpenConsoleExe);
            Assert.Equal(EnableResult.Registered, engine.Enable());

            var report = Revalidate(NewInputs(engine, paths, a, installedExe: true, singleInstance: false));

            Assert.Equal(RevalidationAction.SelfDisabled, report.Action);
            Assert.False(engine.IsRegistered());
            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            Assert.Null(check.OpenSubKey(paths.ClsidSubkeyOf(a.TerminalClsid)));
            Assert.Null(check.OpenSubKey(paths.ClsidSubkeyOf(a.ConsoleClsid)));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void ReportIsSurfaced()
    {
        var root = MintedRoot();
        try
        {
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, a, OpenConsoleExe);
            Assert.Equal(EnableResult.Registered, engine.Enable());

            var report = Revalidate(NewInputs(engine, paths, a, installedExe: true, singleInstance: false));

            Assert.NotEmpty(report.Notice);
            Assert.Contains("single", report.Notice, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("instance", report.Notice, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static RevalidationReport Revalidate(RevalidationInputs inputs) =>
        new DefaultTermRevalidator().Revalidate(inputs);

    private static RevalidationInputs NewInputs(
        DefaultTermRegistration engine,
        DefaultTermRegistryPaths paths,
        TestIdentity identity,
        bool installedExe,
        bool singleInstance = true)
    {
        // The bound-path self-check is simulated: the test decides whether
        // this exe passes it. The exe path fed to the check is the one the
        // engine was constructed with, read from the bound registration.
        return new RevalidationInputs(
            engine,
            PassesBoundPathCheck: installedExe,
            SingleInstanceEnabled: singleInstance);
    }

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

    private static string MintedGuid() => Guid.NewGuid().ToString("B").ToUpperInvariant();

    private static TestIdentity MintedIdentity() => new(
        MintedGuid(), MintedGuid(), MintedGuid(),
        new[] { MintedGuid(), MintedGuid(), MintedGuid() });

    private static string MintedRoot() => TestRootsParent + "\\" + MintedGuid();

    private static DefaultTermRegistration NewEngine(
        DefaultTermRegistryPaths paths, TestIdentity identity, string openConsoleExe) =>
        new(paths, identity, WinttyExe, openConsoleExe, ProxyDll);

    private static string Quote(string path) => "\"" + path + "\"";

    private static void SetStartupValue(
        RegistryKey baseKey, DefaultTermRegistryPaths paths, string valueName, string value)
    {
        using var startup = baseKey.OpenSubKey(paths.StartupSubkey, writable: true);
        Assert.NotNull(startup);
        startup!.SetValue(valueName, value);
    }

    private static void DeleteTestRoot(string root)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        hkcu.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        try
        {
            using var classes = hkcu.OpenSubKey(@"Software\Classes", writable: true);
            classes?.DeleteSubKey("WinttyDefTermTests");
        }
        catch (ArgumentException)
        {
            // Parent not empty: another root outlives this test.
        }
        catch (InvalidOperationException)
        {
            // Another class's parallel root is still in the parent; its own
            // cleanup takes the parent out when it is last.
        }
    }
}
