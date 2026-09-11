// Shared by every C# test project via a linked Compile item (see each
// *.Tests.csproj). One physical file, so the four hosts cannot drift.
//
// Founder rule (2026-09-11): testing of all kinds in this repo must use a
// throwaway temp config, never the real one, enforced in code. The harness
// side arms per launch; THIS is the same rule for the in-process test
// hosts, the one entry kind no launch scan can see (audit M1): nothing is
// ever executed, so the arming has to live in the host process itself.
//
// It runs before any test: [ModuleInitializer] fires when the module
// loads, covering dotnet test, dotnet run, the IDE, and plain vstest alike
// (a runsettings <EnvironmentVariables> block would only cover --settings
// invocations).

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Ghostty.Testing;

internal static class TestHostArming
{
    /// <summary>
    /// The one documented way out, for a test that genuinely needs a clean
    /// host environment. Deliberately loud: the host prints a red-visible
    /// line naming the variable, and a source-scan test forbids it being
    /// set in any committed script, recipe or CI file, so the hatch cannot
    /// quietly become the default.
    /// </summary>
    private const string UnarmedEnvVar = "WINTTY_TEST_HOST_UNARMED";

    private static string? Root;

    [ModuleInitializer]
    public static void Arm()
    {
        if (Environment.GetEnvironmentVariable(UnarmedEnvVar) == "1")
        {
            Console.WriteLine(
                "WINTTY_TEST_HOST_UNARMED=1: this test host runs WITHOUT " +
                "config isolation. Anything it touches in a default config " +
                "path reaches the REAL per-user config.");
            return;
        }

        try
        {
            // The same anchor the app-side guard compares against: the
            // known-folder temp, which the process environment cannot
            // move, so an armed host stays under the boundary even when
            // TEMP/TMP are redirected around it. The name spelling matches
            // the harness helper (lib/test-config.ps1), with this host's
            // own prefix so sweeps can tell them apart.
            var anchor = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(anchor)) anchor = Path.GetTempPath();
            anchor = Path.Combine(anchor, "Temp");

            var bytes = RandomNumberGenerator.GetBytes(16);
            var root = Path.Combine(anchor,
                "wintty-testhost-" + Convert.ToHexString(bytes).ToLowerInvariant());
            Directory.CreateDirectory(root);

            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
            Environment.SetEnvironmentVariable("WINTTY_TEST_CONFIG", "1");
            Root = root;

            // Best effort, and confined to THIS root by construction: the
            // path is anchor + our own random leaf, never a caller-supplied
            // value, so the delete cannot walk outside what this class
            // created.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    if (Root is { } r && Directory.Exists(r))
                        Directory.Delete(r, recursive: true);
                }
                catch
                {
                    // A held file or a host killed mid-run leaves the root
                    // behind; that is leak, not taint, and the anchor sweep
                    // pattern (24h) is the reaper if one is ever wanted.
                }
            };
        }
        catch
        {
            // Arming must never break the host: if the environment cannot
            // be set, the tests run exactly as they did before this class
            // existed. The arming test fails loudly in that case.
        }
    }
}
