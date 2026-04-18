using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Ghostty.Bench.Output;

public sealed record HostInfo(
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("build")] string Build,
    [property: JsonPropertyName("cpu")] string Cpu,
    [property: JsonPropertyName("arch")] string Arch,
    [property: JsonPropertyName("dotnet")] string Dotnet,
    [property: JsonPropertyName("conpty_source")] string ConptySource)
{
    public static HostInfo Capture()
    {
        return new HostInfo(
            Os: OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.OSDescription,
            Build: Environment.OSVersion.Version.Build.ToString(),
            Cpu: CaptureCpuModel(),
            Arch: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            Dotnet: Environment.Version.ToString(),
            ConptySource: "inbox");
    }

    private static string CaptureCpuModel()
    {
        // Best-effort CPU model string. On Windows we read the registry key
        // HKLM\Hardware\Description\System\CentralProcessor\0\ProcessorNameString.
        // On failure we fall back to the architecture name.
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"Hardware\Description\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(name) ? RuntimeInformation.ProcessArchitecture.ToString() : name.Trim();
        }
        catch
        {
            return RuntimeInformation.ProcessArchitecture.ToString();
        }
    }
}
