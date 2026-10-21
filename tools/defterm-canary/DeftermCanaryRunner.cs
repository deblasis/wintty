using Ghostty.Core.DefaultTerm;

namespace Wintty.DeftermCanaryTool;

/// <summary>
/// The suite-level canary wrapper's logic: an outer guard that fails on
/// any real-state delta the suite caused, and the nested kill-test that
/// proves the canary can go red. The kill value's name lives HERE and
/// nowhere else; the scan-gate test enforces that confinement.
/// </summary>
public static class DeftermCanaryRunner
{
    public const string KillValueName = "WinttyDeftermCanaryKillProbe";

    /// <summary>
    /// Snapshot the real root, run the inner action, compare at exit:
    /// any delta in the default-terminal write-set fails the guard.
    /// </summary>
    public static int OuterGuard(Action inner, Action<string>? report = null)
    {
        var canary = new DeftermCanary(
            DefaultTermRegistryPaths.Production,
            DefaultTermIdentityView.Instance);
        var before = canary.Snapshot();

        var innerExit = 0;
        try
        {
            inner();
        }
        catch (Exception e)
        {
            innerExit = 1;
            report?.Invoke("inner failed: " + e.Message);
        }

        var delta = DeftermCanary.Diff(before, canary.Snapshot());
        foreach (var line in delta)
        {
            report?.Invoke(line);
        }

        return innerExit == 0 && delta.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The kill-test: one deliberate write to one named value through
    /// the library, the compare must go red with the library's own
    /// comparator (pinned by reference; a substitute is itself a
    /// failure), the write restored, leaving the watched set clean.
    /// </summary>
    public static int KillTest(
        DeftermCanary canary,
        Func<DeftermSnapshot, DeftermSnapshot, IReadOnlyList<string>> comparator,
        string valueName)
    {
        if (!ReferenceEquals(comparator, DeftermCanary.Diff))
        {
            return 1;
        }

        var before = canary.Snapshot();
        canary.WriteStartupProbe(valueName, "probe");
        try
        {
            return comparator(before, canary.Snapshot()).Count > 0 ? 0 : 1;
        }
        finally
        {
            canary.DeleteStartupProbe(valueName);
        }
    }
}
