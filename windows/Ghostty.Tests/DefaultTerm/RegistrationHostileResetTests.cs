using System.Runtime.Versioning;
using Ghostty.Core.DefaultTerm;
using Microsoft.Win32;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The enable path against pre-seeded hostile keys and injected write
/// faults, on a throwaway root. Enable must leave either the exact write
/// set or the exact pre-existing state: a half-registration is never an
/// outcome, a hostile value that survives the sweep is never an outcome,
/// and an undeletable key fails closed with nothing written.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistrationHostileResetTests
{
    private const string WinttyExe = @"C:\Program Files\Wintty\Wintty.exe";
    private const string OpenConsoleExe = @"C:\Program Files\Wintty\bin\openconsole.exe";
    private const string ProxyDll = @"C:\Program Files\Wintty\bin\winttyproxy.dll";

    private const string TestRootsParent = @"Software\Classes\WinttyDefTermTests";

    [Fact]
    public void TreatAs_IsSweptByEnable()
    {
        var root = MintedRoot();
        try
        {
            var identity = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var terminalKey = paths.ClsidSubkeyOf(identity.TerminalClsid);

            // The attacker's pre-seed: a TreatAs redirect, a display-name
            // default, a hostile server command with trailing arguments,
            // and an unrelated subkey.
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            using (var hostile = hkcu.CreateSubKey(terminalKey))
            {
                hostile.SetValue("TreatAs", MintedGuid());
                hostile.SetValue(null, "hostile display name");
                using var server = hostile.CreateSubKey("LocalServer32");
                server.SetValue(null, @"C:\attacker\wintty.exe /takeover");
                using var bogus = hostile.CreateSubKey("Bogus");
                bogus.SetValue("payload", "x");
            }

            var engine = NewEngine(paths, identity);
            Assert.Equal(EnableResult.Registered, engine.Enable());

            using var check = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using (var swept = check.OpenSubKey(terminalKey))
            {
                Assert.NotNull(swept);
                // Exactly the values enable writes: no TreatAs, no display
                // name, no Bogus subkey.
                Assert.Equal(new[] { "LocalServer32" }, swept!.GetSubKeyNames());
                Assert.Null(swept.GetValue("TreatAs"));
                Assert.Null(swept.GetValue(null));
            }

            AssertLocalServer(check, terminalKey, Quote(WinttyExe));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void UndeletableKey_FailsClosed()
    {
        var root = MintedRoot();
        try
        {
            var identity = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var terminalKey = paths.ClsidSubkeyOf(identity.TerminalClsid);

            var preState = new List<string>();
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                using var hostile = hkcu.CreateSubKey(terminalKey);
                hostile.SetValue("TreatAs", MintedGuid());
                preState = CollectSubtree(hkcu, root);
            }

            var engine = NewEngine(paths, identity);
            engine.ResetFault = path =>
                path == terminalKey ? new InvalidOperationException("injected: undeletable") : null;

            var result = engine.Enable();

            Assert.Equal(EnableStatus.Failed, result.Status);
            Assert.Equal(EnableFailureKind.UndeletableKey, result.FailureKind);
            Assert.Equal(terminalKey, result.FailedKey);

            // Fail closed: the pre-seed is intact and nothing was written.
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            {
                Assert.Equal(
                    preState.OrderBy(p => p, StringComparer.Ordinal),
                    CollectSubtree(hkcu, root).OrderBy(p => p, StringComparer.Ordinal));
            }
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void FailureAtStepN_RollsBack()
    {
        // One clean enable to learn the ordered value-write sequence, then
        // a fresh root per step with the fault armed at that step. The
        // mid-selection case (between the two startup values) is a step
        // like any other and must roll the first value back.
        List<(string Subkey, string? ValueName)> sequence = LearnValueWriteSequence();

        Assert.Contains(sequence, w =>
            w.Subkey.EndsWith(@"\Console\%%Startup", StringComparison.Ordinal)
            && w.ValueName == DefaultTermRegistration.DelegationConsoleValue);
        Assert.Contains(sequence, w =>
            w.Subkey.EndsWith(@"\Console\%%Startup", StringComparison.Ordinal)
            && w.ValueName == DefaultTermRegistration.DelegationTerminalValue);

        for (var step = 0; step < sequence.Count; step++)
        {
            var root = MintedRoot();
            try
            {
                var identity = MintedIdentity();
                var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
                var engine = NewEngine(paths, identity);

                var fired = 0;
                engine.ValueWriteFault = (_, _) => ++fired == step + 1
                    ? new InvalidOperationException("injected: write fault")
                    : null;

                var result = engine.Enable();

                Assert.True(result.Status == EnableStatus.Failed, $"step {step}: expected failure");
                Assert.Equal(EnableFailureKind.WriteFailed, result.FailureKind);

                using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                var values = CollectValues(hkcu, root);
                Assert.True(values.Count == 0,
                    $"step {step} ({sequence[step].Subkey}:{sequence[step].ValueName}) left {values.Count} values behind");
            }
            finally
            {
                DeleteTestRoot(root);
            }
        }
    }

    [Fact]
    public void WriteOrder_ConsoleValueBeforeTerminalValue()
    {
        var root = MintedRoot();
        try
        {
            var identity = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, identity);

            var writes = new List<(string Subkey, string? ValueName)>();
            engine.ValueWriteObserver = (subkey, valueName) => writes.Add((subkey, valueName));

            Assert.Equal(EnableResult.Registered, engine.Enable());

            var consoleAt = writes.FindIndex(w =>
                w.Subkey == paths.StartupSubkey
                && w.ValueName == DefaultTermRegistration.DelegationConsoleValue);
            var terminalAt = writes.FindIndex(w =>
                w.Subkey == paths.StartupSubkey
                && w.ValueName == DefaultTermRegistration.DelegationTerminalValue);

            Assert.True(consoleAt >= 0, "console value never written");
            Assert.True(terminalAt >= 0, "terminal value never written");
            Assert.True(consoleAt < terminalAt,
                "the console value must land before the terminal value inside the selection key");
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private List<(string Subkey, string? ValueName)> LearnValueWriteSequence()
    {
        var root = MintedRoot();
        try
        {
            var identity = MintedIdentity();
            var paths = DefaultTermRegistryPaths.UnderTestRoot(root);
            var engine = NewEngine(paths, identity);

            var writes = new List<(string Subkey, string? ValueName)>();
            engine.ValueWriteObserver = (subkey, valueName) => writes.Add((subkey, valueName));
            Assert.Equal(EnableResult.Registered, engine.Enable());
            return writes;
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
    }

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
            // Another class's parallel root is still in the parent; the
            // non-recursive delete refuses. Its own cleanup takes the
            // parent out when it is last.
        }
    }
}
