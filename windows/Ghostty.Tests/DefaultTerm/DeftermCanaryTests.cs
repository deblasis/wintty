using Ghostty.Core.DefaultTerm;
using Wintty.DeftermCanaryTool;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The machine-state canary: snapshots the real write-set key list and
/// diffs, its kill-test refuses a comparator that is not the library's
/// own, and the scan gate keeps real-root references out of every
/// dev-runnable test source. No test in this file writes real state:
/// everything runs under a throwaway registry root.
/// </summary>
public sealed class DeftermCanaryTests : IDisposable
{
    private readonly string _root;
    private readonly DeftermCanary _canary;

    public DeftermCanaryTests()
    {
        _root = @"HKEY_CURRENT_USER\Software\WinttyDeftermCanaryTests\"
            + Guid.NewGuid().ToString("N");
        using (Microsoft.Win32.RegistryKey.OpenBaseKey(
                   Microsoft.Win32.RegistryHive.CurrentUser,
                   Microsoft.Win32.RegistryView.Default)
               .CreateSubKey(_root.Substring("HKEY_CURRENT_USER\\".Length)))
        {
        }

        _canary = new DeftermCanary(
            DefaultTermRegistryPaths.UnderTestRoot(_root),
            DefaultTermIdentityView.Instance);
    }

    public void Dispose()
    {
        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
            Microsoft.Win32.RegistryHive.CurrentUser,
            Microsoft.Win32.RegistryView.Default);
        baseKey.DeleteSubKeyTree(
            _root.Substring("HKEY_CURRENT_USER\\".Length), throwOnMissingSubKey: false);
    }

    [Fact]
    public void Snapshot_DetectsSingleValueChange()
    {
        var before = _canary.Snapshot();
        Assert.Empty(DeftermCanary.Diff(before, _canary.Snapshot()));

        _canary.WriteStartupProbe("test-probe", "1");
        var withProbe = _canary.Snapshot();
        var delta = DeftermCanary.Diff(before, withProbe);
        var line = Assert.Single(delta);
        Assert.Contains("test-probe", line);

        _canary.DeleteStartupProbe("test-probe");
        Assert.Empty(DeftermCanary.Diff(before, _canary.Snapshot()));
    }

    [Fact]
    public void KillTest_SubstituteComparator_Fails()
    {
        // A comparator that is not the library's own must fail the
        // kill-test outright: identity is pinned, not behavior-probed.
        IReadOnlyList<string> AlwaysClean(
            DeftermSnapshot a, DeftermSnapshot b) => Array.Empty<string>();
        Assert.Equal(1, DeftermCanaryRunner.KillTest(
            _canary, AlwaysClean, "test-kill"));

        // The library's own comparator passes and restores.
        Assert.Equal(0, DeftermCanaryRunner.KillTest(
            _canary, DeftermCanary.Diff, "test-kill"));
        Assert.Empty(DeftermCanary.Diff(_canary.Snapshot(), _canary.Snapshot()));
    }

    [Fact]
    public void ScanGate_RealRootReferences_BannedInTestSources()
    {
        var root = RepoRoot();
        var testSources = Directory.GetFiles(
            Path.Combine(root, "windows", "Ghostty.Tests", "DefaultTerm"),
            "*.cs", SearchOption.AllDirectories);

        Assert.NotEmpty(testSources);
        // The needles are built from halves so this scan's own source
        // does not carry the banned literals.
        var productionNeedle = "DefaultTermRegistryPaths" + ".Production";
        var previewConsoleClsid = "06EC847C" + "-C0A5-46B8-92CB-7C92F6E35CD5";
        var stableConsoleClsid = "2EACA947" + "-7F5F-4CFA-BA87-8F7FBEEFBE69";
        foreach (var file in testSources)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(productionNeedle, text);
            Assert.DoesNotContain(previewConsoleClsid, text);
            Assert.DoesNotContain(stableConsoleClsid, text);
        }
    }

    [Fact]
    public void Allowlist_ConfinedToWrapperAssembly()
    {
        var root = RepoRoot();
        var wrapperDir = Path.Combine(root, "tools", "defterm-canary");
        var wrapperSources = Directory.GetFiles(wrapperDir, "*.cs");
        Assert.NotEmpty(wrapperSources);

        // The kill value's name is defined once, in the wrapper, and
        // appears nowhere else under windows/ or tools/.
        var wrapperText = File.ReadAllText(
            Path.Combine(wrapperDir, "DeftermCanaryRunner.cs"));
        const string marker = "const string KillValueName = \"";
        var at = wrapperText.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "the wrapper must define KillValueName");
        var nameStart = at + marker.Length;
        var nameEnd = wrapperText.IndexOf('"', nameStart);
        var killName = wrapperText[nameStart..nameEnd];
        Assert.False(string.IsNullOrWhiteSpace(killName));

        foreach (var dir in new[] { "windows", "tools" })
        {
            foreach (var file in Directory.GetFiles(
                         Path.Combine(root, dir), "*.cs",
                         SearchOption.AllDirectories))
            {
                if (file.StartsWith(wrapperDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Assert.DoesNotContain(
                    killName,
                    File.ReadAllText(file));
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "build.zig")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
