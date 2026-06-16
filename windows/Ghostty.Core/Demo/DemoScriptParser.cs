#if DEMO
using System;
using System.IO;
using System.Text.Json;

namespace Ghostty.Core.Demo;

/// <summary>
/// Parses demo scripts and resolves where the script file lives. Pure and
/// side-effect free (file existence is injected) so both halves are unit-tested.
/// </summary>
internal static class DemoScriptParser
{
    /// <summary>Deserialize a demo script. Throws on malformed JSON.</summary>
    public static DemoScript Parse(string json)
    {
        var script = JsonSerializer.Deserialize(json, DemoJsonContext.Default.DemoScript);
        return script ?? throw new JsonException("Demo script deserialized to null.");
    }

    /// <summary>
    /// Resolve the script path by precedence:
    ///   1. <paramref name="envValue"/> if it points at an existing file,
    ///   2. demo.json next to the executable,
    ///   3. demo.json under the config dir's wintty/ folder.
    /// Returns null when none exist.
    /// </summary>
    public static string? ResolveScriptPath(
        string? envValue,
        string exeDir,
        string? configDir,
        Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(envValue) && fileExists(envValue))
            return envValue;

        var beside = Path.Combine(exeDir, "demo.json");
        if (fileExists(beside))
            return beside;

        if (!string.IsNullOrWhiteSpace(configDir))
        {
            var inConfig = Path.Combine(configDir, "wintty", "demo.json");
            if (fileExists(inConfig))
                return inConfig;
        }

        return null;
    }
}
#endif
