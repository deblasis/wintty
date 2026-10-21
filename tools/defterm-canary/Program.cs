using Ghostty.Core.DefaultTerm;
using Wintty.DeftermCanaryTool;

// The default-terminal machine-state canary. Invoked by the tier
// harness around the dev-runnable suite: the outer guard fails on any
// real write-set delta the suite caused, and each invocation also runs
// the kill-test, proving the canary can go red on a real write that is
// made and restored through the library itself.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--kill-test-only"))
        {
            return DeftermCanaryRunner.KillTest(
                NewCanary(),
                DeftermCanary.Diff,
                DeftermCanaryRunner.KillValueName);
        }

        return DeftermCanaryRunner.OuterGuard(() =>
        {
            var killed = DeftermCanaryRunner.KillTest(
                NewCanary(),
                DeftermCanary.Diff,
                DeftermCanaryRunner.KillValueName);
            if (killed != 0)
            {
                throw new InvalidOperationException("kill-test failed");
            }
        }, Console.Error.WriteLine);
    }

    private static DeftermCanary NewCanary() => new(
        DefaultTermRegistryPaths.Production,
        DefaultTermIdentityView.Instance);
}
