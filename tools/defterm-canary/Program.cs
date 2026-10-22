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

        // Suite-wrapping mode: everything after "--" is the suite this
        // guard runs around, so a harness (or a person) gets the
        // outer snapshot -> inner suite -> outer compare contract:
        //   defterm-canary -- dotnet test ...
        var separator = Array.IndexOf(args, "--");
        if (separator >= 0)
        {
            var command = args[(separator + 1)..];
            if (command.Length == 0)
            {
                Console.Error.WriteLine("nothing to run after --");
                return 2;
            }

            return DeftermCanaryRunner.OuterGuard(() =>
            {
                var info = new System.Diagnostics.ProcessStartInfo(
                    command[0],
                    string.Join(' ', command.Skip(1)))
                {
                    UseShellExecute = false,
                };
                using var process = System.Diagnostics.Process.Start(info)
                    ?? throw new InvalidOperationException("failed to start the inner suite");
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "inner suite exited " + process.ExitCode);
                }
            }, Console.Error.WriteLine);
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
