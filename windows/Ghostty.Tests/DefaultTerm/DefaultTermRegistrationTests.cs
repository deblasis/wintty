using System.Runtime.Versioning;
using Ghostty.Core.DefaultTerm;
using Microsoft.Win32;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The registration engine's enable path, run against a throwaway
/// registry root. Every test mints its own root GUID and its own handler
/// GUIDs and hands the engine <see cref="DefaultTermRegistryPaths.UnderTestRoot"/>,
/// so the real Classes namespace and the real Console delegation key are
/// never written. The first test carries the leak oracle: a write that
/// dropped the root prefix would surface as minted GUIDs in the real
/// namespace or as the real startup pair's values, and the exact-subtree
/// check would show the same defect as a missing key under the root.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DefaultTermRegistrationTests
{
    private const string WinttyExe = @"C:\Program Files\Wintty\Wintty.exe";
    private const string OpenConsoleExe = @"C:\Program Files\Wintty\bin\openconsole.exe";
    private const string ProxyDll = @"C:\Program Files\Wintty\bin\winttyproxy.dll";

    private const string TestRootsParent = @"Software\Classes\WinttyDefTermTests";

    [Fact]
    public void Enable_WritesExactWriteSet()
    {
        var root = MintedRoot();
        try
        {
            var identity = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, identity);

            Assert.Equal(EnableResult.Registered, engine.Enable());
            Assert.Equal(EnableResult.AlreadyRegistered, engine.Enable());

            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

            AssertLocalServer(hkcu, paths.ClsidSubkeyOf(identity.TerminalClsid), Quote(WinttyExe));
            AssertLocalServer(hkcu, paths.ClsidSubkeyOf(identity.ConsoleClsid), Quote(OpenConsoleExe));

            using (var inproc = hkcu.OpenSubKey(paths.ClsidSubkeyOf(identity.ProxyClsid) + @"\InprocServer32"))
            {
                Assert.NotNull(inproc);
                Assert.Equal(ProxyDll, (string?)inproc!.GetValue(null));
                Assert.Equal(RegistryValueKind.String, inproc.GetValueKind(null));
                Assert.Equal("Both", (string?)inproc.GetValue(DefaultTermRegistration.ThreadingModelValue));
            }

            foreach (var iid in identity.HandoffIids)
            {
                using var proxyStub = hkcu.OpenSubKey(paths.InterfaceSubkeyOf(iid) + @"\ProxyStubClsid32");
                Assert.NotNull(proxyStub);
                Assert.Equal(identity.ProxyClsid, (string?)proxyStub!.GetValue(null));
            }

            using (var startup = hkcu.OpenSubKey(paths.StartupSubkey))
            {
                Assert.NotNull(startup);
                Assert.Equal(identity.ConsoleClsid, (string?)startup!.GetValue(DefaultTermRegistration.DelegationConsoleValue));
                Assert.Equal(identity.TerminalClsid, (string?)startup.GetValue(DefaultTermRegistration.DelegationTerminalValue));
            }

            // The subtree under the minted root is exactly the write set:
            // every key the write set calls for, and nothing else.
            var expected = ExpectedSubtree(root, identity, paths);
            var actual = CollectSubtree(hkcu, root);
            Assert.Equal(
                expected.OrderBy(p => p, StringComparer.Ordinal),
                actual.OrderBy(p => p, StringComparer.Ordinal));
            Assert.All(actual, path => Assert.True(
                path == root || path.StartsWith(root + @"\", StringComparison.Ordinal),
                $"path outside the minted root: {path}"));

            AssertNoLeakIntoRealNamespace(hkcu, identity);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void WriteOrder_BindingsBeforeSelection()
    {
        var root = MintedRoot();
        try
        {
            var identity = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, identity);

            var written = new List<string>();
            engine.WriteObserver = written.Add;

            Assert.Equal(EnableResult.Registered, engine.Enable());

            // The selection pair lands once, and last, after every binding:
            // a reader watching the root never sees the pair pointing at
            // handlers that are not registered yet.
            Assert.Equal(paths.StartupSubkey, written[^1]);
            Assert.Equal(1, written.Count(path => path == paths.StartupSubkey));

            var beforeSelection = written.Take(written.Count - 1).ToList();
            Assert.Contains(paths.ClsidSubkeyOf(identity.TerminalClsid) + @"\LocalServer32", beforeSelection);
            Assert.Contains(paths.ClsidSubkeyOf(identity.ConsoleClsid) + @"\LocalServer32", beforeSelection);
            Assert.Contains(paths.ClsidSubkeyOf(identity.ProxyClsid) + @"\InprocServer32", beforeSelection);
            foreach (var iid in identity.HandoffIids)
                Assert.Contains(paths.InterfaceSubkeyOf(iid) + @"\ProxyStubClsid32", beforeSelection);

            // One clean pass: nothing is written twice.
            Assert.Equal(written.Count, written.Distinct().Count());
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void IsRegistered_MatchesOwnPairOnly()
    {
        var root = MintedRoot();
        try
        {
            var identity = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, identity);

            Assert.False(engine.IsRegistered());

            Assert.Equal(EnableResult.Registered, engine.Enable());
            Assert.True(engine.IsRegistered());

            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

            // A foreign terminal on our slot is not us.
            SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationTerminalValue, MintedGuid());
            Assert.False(engine.IsRegistered());

            // Neither is a foreign console on the other slot.
            SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationConsoleValue, MintedGuid());
            Assert.False(engine.IsRegistered());

            // Our own pair matches again only once both halves are ours.
            SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationTerminalValue, identity.TerminalClsid);
            Assert.False(engine.IsRegistered());
            SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationConsoleValue, identity.ConsoleClsid);
            Assert.True(engine.IsRegistered());

            // And with the selection gone there is nothing to match.
            hkcu.DeleteSubKeyTree(paths.StartupSubkey);
            Assert.False(engine.IsRegistered());
        }
        finally
        {
            DeleteTestRoot(root);
        }
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

    private static DefaultTermRegistration NewEngine(DefaultTermRegistryPaths paths, TestIdentity identity) =>
        new(paths, identity, WinttyExe, OpenConsoleExe, ProxyDll);

    private static string Quote(string path) => "\"" + path + "\"";

    private static void AssertLocalServer(RegistryKey baseKey, string clsidSubkey, string expectedCommand)
    {
        using var server = baseKey.OpenSubKey(clsidSubkey + @"\LocalServer32");
        Assert.NotNull(server);
        Assert.Equal(expectedCommand, (string?)server!.GetValue(null));
        Assert.Equal(RegistryValueKind.String, server.GetValueKind(null));
    }

    private static void SetStartupValue(
        RegistryKey baseKey, DefaultTermRegistryPaths paths, string valueName, string value)
    {
        using var startup = baseKey.OpenSubKey(paths.StartupSubkey, writable: true);
        Assert.NotNull(startup);
        startup!.SetValue(valueName, value);
    }

    private static string[] ExpectedSubtree(
        string root, TestIdentity identity, DefaultTermRegistryPaths paths) =>
    [
        root,
        root + @"\Software",
        root + @"\Software\Classes",
        root + @"\Software\Classes\CLSID",
        paths.ClsidSubkeyOf(identity.TerminalClsid),
        paths.ClsidSubkeyOf(identity.TerminalClsid) + @"\LocalServer32",
        paths.ClsidSubkeyOf(identity.ConsoleClsid),
        paths.ClsidSubkeyOf(identity.ConsoleClsid) + @"\LocalServer32",
        paths.ClsidSubkeyOf(identity.ProxyClsid),
        paths.ClsidSubkeyOf(identity.ProxyClsid) + @"\InprocServer32",
        root + @"\Software\Classes\Interface",
        paths.InterfaceSubkeyOf(identity.HandoffIids[0]),
        paths.InterfaceSubkeyOf(identity.HandoffIids[0]) + @"\ProxyStubClsid32",
        paths.InterfaceSubkeyOf(identity.HandoffIids[1]),
        paths.InterfaceSubkeyOf(identity.HandoffIids[1]) + @"\ProxyStubClsid32",
        paths.InterfaceSubkeyOf(identity.HandoffIids[2]),
        paths.InterfaceSubkeyOf(identity.HandoffIids[2]) + @"\ProxyStubClsid32",
        root + @"\Console",
        paths.StartupSubkey,
    ];

    private static List<string> CollectSubtree(RegistryKey baseKey, string rootSubkey)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootSubkey);
        while (pending.Count > 0)
        {
            var path = pending.Pop();
            using var key = baseKey.OpenSubKey(path);
            if (key is null)
            {
                continue;
            }

            found.Add(path);
            foreach (var child in key.GetSubKeyNames())
            {
                pending.Push(path + "\\" + child);
            }
        }

        return found;
    }

    private static void AssertNoLeakIntoRealNamespace(RegistryKey baseKey, TestIdentity identity)
    {
        foreach (var clsid in new[] { identity.TerminalClsid, identity.ConsoleClsid, identity.ProxyClsid })
        {
            Assert.Null(baseKey.OpenSubKey(@"Software\Classes\CLSID\" + clsid));
        }

        foreach (var iid in identity.HandoffIids)
        {
            Assert.Null(baseKey.OpenSubKey(@"Software\Classes\Interface\" + iid));
        }

        using var realStartup = baseKey.OpenSubKey(@"Console\%%Startup");
        if (realStartup is null)
        {
            return;
        }

        Assert.NotEqual(identity.ConsoleClsid, (string?)realStartup.GetValue(DefaultTermRegistration.DelegationConsoleValue));
        Assert.NotEqual(identity.TerminalClsid, (string?)realStartup.GetValue(DefaultTermRegistration.DelegationTerminalValue));
    }

    private static void DeleteTestRoot(string root)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        hkcu.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        try
        {
            // Take the minted parent back out too when this test left it
            // empty; the delete refuses while another test's root is still
            // in there, which is the only outcome we want for it.
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
