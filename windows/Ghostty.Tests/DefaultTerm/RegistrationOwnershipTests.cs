using System.Runtime.Versioning;
using Ghostty.Core.DefaultTerm;
using Microsoft.Win32;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// Shared-key ownership and the restore-correspondence case table, on a
/// throwaway root per test. The engine instances play editions: edition A
/// and B are separate minted identities against the same root, so the
/// cascade tests exercise the real multi-edition interleave without a
/// second build.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistrationOwnershipTests
{
    private const string WinttyExe = @"C:\Program Files\Wintty\Wintty.exe";
    private const string OpenConsoleExe = @"C:\Program Files\Wintty\bin\openconsole.exe";
    private const string ProxyDll = @"C:\Program Files\Wintty\bin\winttyproxy.dll";
    private const string BOpenConsoleExe = @"C:\Program Files\WinttyB\bin\openconsole.exe";

    private const string TestRootsParent = @"Software\Classes\WinttyDefTermTests";

    [Fact]
    public void NonOwningDisable_LeavesSharedGroupAndSelection()
    {
        var root = MintedRoot();
        try
        {
            var a = MintedIdentity();
            var b = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engineB = NewEngine(paths, b, BOpenConsoleExe);
            Assert.Equal(EnableResult.Registered, engineB.Enable());

            // A enables on top of B, then the selection moves back to B's
            // pair via the OS path: A owns neither the selection nor (per
            // B's binaries in the group) the shared keys it did not write.
            var engineA = NewEngine(paths, a, OpenConsoleExe);
            Assert.Equal(EnableResult.Registered, engineA.Enable());
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationConsoleValue, b.ConsoleClsid);
                SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationTerminalValue, b.TerminalClsid);
            }

            Assert.Equal(DisableResult.NotRegistered, engineA.Disable());

            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);

            // B's keys and B's selection survive untouched.
            using (var startup = check.OpenSubKey(paths.StartupSubkey))
            {
                Assert.NotNull(startup);
                Assert.Equal(b.ConsoleClsid, (string?)startup!.GetValue(DefaultTermRegistration.DelegationConsoleValue));
                Assert.Equal(b.TerminalClsid, (string?)startup.GetValue(DefaultTermRegistration.DelegationTerminalValue));
            }

            Assert.NotNull(check.OpenSubKey(paths.ClsidSubkeyOf(b.TerminalClsid)));
            Assert.NotNull(check.OpenSubKey(paths.ClsidSubkeyOf(b.ConsoleClsid)));

            // A's own-outright terminal key is gone; everything else A
            // wrote that A still owns (its own console binding) was
            // removed with the group sweep.
            Assert.Null(check.OpenSubKey(paths.ClsidSubkeyOf(a.TerminalClsid)));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void ShadowUnwrite_NoCorrespondingPrior_RemovesGroupOnly()
    {
        var root = MintedRoot();
        try
        {
            var thirdParty = MintedGuid();
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);

            // A enables (no prior captured), the selection moves to a
            // third party: A still owns the console binding it wrote, so
            // the lingering shadow is unwritten (group removed), while the
            // third-party selection survives untouched.
            var engineA = NewEngine(paths, a, OpenConsoleExe);
            Assert.Equal(EnableResult.Registered, engineA.Enable());
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationTerminalValue, thirdParty);
            }

            Assert.Equal(DisableResult.NotRegistered, engineA.Disable());

            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using (var startup = check.OpenSubKey(paths.StartupSubkey))
            {
                Assert.NotNull(startup);
                Assert.Equal(thirdParty, (string?)startup!.GetValue(DefaultTermRegistration.DelegationTerminalValue));
            }

            Assert.Null(check.OpenSubKey(paths.ClsidSubkeyOf(a.ConsoleClsid)));
            Assert.Null(check.OpenSubKey(paths.ClsidSubkeyOf(a.ProxyClsid)));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void NilPrior_AlwaysRemoves()
    {
        var root = MintedRoot();
        try
        {
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, a, OpenConsoleExe);

            Assert.Equal(EnableResult.Registered, engine.Enable());
            Assert.Equal(DisableResult.Cleared, engine.Disable());

            using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            Assert.Null(hkcu.OpenSubKey(paths.ClsidSubkeyOf(a.ConsoleClsid)));
            Assert.Null(hkcu.OpenSubKey(paths.ClsidSubkeyOf(a.ProxyClsid)));
            foreach (var iid in a.HandoffIids)
            {
                Assert.Null(hkcu.OpenSubKey(paths.InterfaceSubkeyOf(iid)));
            }
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void FiveStepCascade_EndState()
    {
        var root = MintedRoot();
        try
        {
            var a = MintedIdentity();
            var b = MintedIdentity();
            var thirdParty = MintedGuid();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);

            var engineA = NewEngine(paths, a, OpenConsoleExe);
            var engineB = NewEngine(paths, b, BOpenConsoleExe);

            // Steps 1-2: A enables, then B enables over A (B captures A's
            // shared values as its prior).
            Assert.Equal(EnableResult.Registered, engineA.Enable());
            Assert.Equal(EnableResult.Registered, engineB.Enable());

            // Step 3: the selection moves to a third party via the OS path
            // (the pair is rewritten; the shared keys stay as B wrote them).
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationConsoleValue, MintedGuid());
                SetStartupValue(hkcu, paths, DefaultTermRegistration.DelegationTerminalValue, thirdParty);
            }

            // Step 4: A disables. A does not own the selection (the pair
            // names the third party), so this is a shadow-unwrite for A's
            // own bindings: NotRegistered, and A's keys leave.
            Assert.Equal(DisableResult.NotRegistered, engineA.Disable());

            // Step 5: B disables. B does not own the selection either (the
            // third party took it); B still owns the group it wrote, no
            // captured prior corresponds to the third-party terminal, so
            // the group is removed and the third-party selection survives.
            Assert.Equal(DisableResult.NotRegistered, engineB.Disable());

            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using (var startup = check.OpenSubKey(paths.StartupSubkey))
            {
                Assert.NotNull(startup);
                Assert.Equal(thirdParty, (string?)startup!.GetValue(DefaultTermRegistration.DelegationTerminalValue));
            }

            // The shared group is gone (B's keys removed; A's terminal key
            // was already gone with step 4), and nothing dangles: the only
            // remaining values under the root are the third-party pair.
            var values = CollectValues(check, root);
            Assert.All(values, v => Assert.True(v.StartsWith(paths.StartupSubkey, StringComparison.Ordinal),
                $"dangling value outside the selection key: {v}"));
            Assert.Equal(2, values.Count);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void StalePrior_DeadBinaries_FallsBackToNil()
    {
        var root = MintedRoot();
        var path = Path.Combine(Path.GetTempPath(), "WinttyDefTermTests", MintedGuid() + ".json");
        try
        {
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);

            // A pre-existing selection whose console binary no longer
            // exists: the captured prior is stale, and an owner-disable
            // must write the nil pair rather than restore a dead default.
            var priorConsole = MintedGuid();
            var priorTerminal = MintedGuid();
            var deadBinary = @"C:\does\not\exist\openconsole.exe";
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                using var startup = hkcu.CreateSubKey(paths.StartupSubkey);
                startup.SetValue(DefaultTermRegistration.DelegationConsoleValue, priorConsole);
                startup.SetValue(DefaultTermRegistration.DelegationTerminalValue, priorTerminal);
                using var console = hkcu.CreateSubKey(paths.ClsidSubkeyOf(a.ConsoleClsid) + @"\LocalServer32");
                console.SetValue(null, Quote(deadBinary));
            }

            var engine = NewEngine(paths, a, OpenConsoleExe, path);
            Assert.Equal(EnableResult.Registered, engine.Enable());
            Assert.Equal(DisableResult.Cleared, engine.Disable());

            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var startupAfter = check.OpenSubKey(paths.StartupSubkey);
            Assert.NotNull(startupAfter);
            Assert.Equal(
                "{00000000-0000-0000-0000-000000000000}",
                (string?)startupAfter!.GetValue(DefaultTermRegistration.DelegationConsoleValue));
            Assert.Equal(
                "{00000000-0000-0000-0000-000000000000}",
                (string?)startupAfter.GetValue(DefaultTermRegistration.DelegationTerminalValue));
        }
        finally
        {
            DeleteTestRoot(root);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void CaptureStore_CorruptFile_IsNil()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinttyDefTermTests", MintedGuid() + ".json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json ]");

            var store = new DefaultTermCaptureStore(path);
            var loaded = store.Load();

            Assert.False(loaded.HasSelectionPrior);
            Assert.Empty(loaded.SharedGroupValues);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void CaptureStore_GoodRestorePathIsAsserted()
    {
        var root = MintedRoot();
        var path = Path.Combine(Path.GetTempPath(), "WinttyDefTermTests", MintedGuid() + ".json");
        try
        {
            var a = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);

            // A pre-existing selection with a live console binary: the
            // enable captures it, and the owner-disable restores it. This
            // is the test a never-restore mutant fails: the restored
            // values are asserted to equal the captured prior exactly.
            var priorConsole = MintedGuid();
            var priorTerminal = MintedGuid();
            var liveBinary = Environment.SystemDirectory + @"\cmd.exe";
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                using var startup = hkcu.CreateSubKey(paths.StartupSubkey);
                startup.SetValue(DefaultTermRegistration.DelegationConsoleValue, priorConsole);
                startup.SetValue(DefaultTermRegistration.DelegationTerminalValue, priorTerminal);
                using var console = hkcu.CreateSubKey(paths.ClsidSubkeyOf(a.ConsoleClsid) + @"\LocalServer32");
                console.SetValue(null, Quote(liveBinary));
            }

            var engine = NewEngine(paths, a, OpenConsoleExe, path);
            Assert.Equal(EnableResult.Registered, engine.Enable());
            Assert.Equal(DisableResult.Cleared, engine.Disable());

            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using (var restored = check.OpenSubKey(paths.StartupSubkey))
            {
                Assert.NotNull(restored);
                Assert.Equal(priorConsole, (string?)restored!.GetValue(DefaultTermRegistration.DelegationConsoleValue));
                Assert.Equal(priorTerminal, (string?)restored.GetValue(DefaultTermRegistration.DelegationTerminalValue));
            }

            using var consoleAfter = check.OpenSubKey(paths.ClsidSubkeyOf(a.ConsoleClsid) + @"\LocalServer32");
            Assert.NotNull(consoleAfter);
            Assert.Equal(Quote(liveBinary), (string?)consoleAfter!.GetValue(null));
        }
        finally
        {
            DeleteTestRoot(root);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
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

    private static DefaultTermRegistration NewEngine(
        DefaultTermRegistryPaths paths, TestIdentity identity, string openConsoleExe, string? storePath = null) =>
        storePath is null
            ? new DefaultTermRegistration(paths, identity, WinttyExe, openConsoleExe, ProxyDll)
            : new DefaultTermRegistration(paths, identity, WinttyExe, openConsoleExe, ProxyDll, storePath);

    private static string Quote(string path) => "\"" + path + "\"";

    private static void SetLocalServer(RegistryKey baseKey, string clsidSubkey, string command)
    {
        using var server = baseKey.OpenSubKey(clsidSubkey + @"\LocalServer32", writable: true);
        Assert.NotNull(server);
        server!.SetValue(null, command);
    }

    private static void SetStartupValue(
        RegistryKey baseKey, DefaultTermRegistryPaths paths, string valueName, string value)
    {
        using var startup = baseKey.OpenSubKey(paths.StartupSubkey, writable: true);
        Assert.NotNull(startup);
        startup!.SetValue(valueName, value);
    }

    private static List<string> CaptureState(string root)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        var state = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var path = pending.Pop();
            using var key = hkcu.OpenSubKey(path);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetValueNames())
            {
                state.Add(path + ":" + (name ?? "(default)") + "=" + key.GetValue(name));
            }

            foreach (var child in key.GetSubKeyNames())
            {
                pending.Push(path + "\\" + child);
            }
        }

        return state;
    }

    private static List<string> CollectValues(RegistryKey baseKey, string rootSubkey)
    {
        var values = new List<string>();
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

            foreach (var name in key.GetValueNames())
            {
                values.Add(path + ":" + (name ?? "(default)"));
            }

            foreach (var child in key.GetSubKeyNames())
            {
                pending.Push(path + "\\" + child);
            }
        }

        return values;
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
