using System.Runtime.InteropServices;
using System.Text.Json;
using Ghostty.Bench.Output;
using Ghostty.Bench.Probes;
using Ghostty.Bench.Transports;

namespace Ghostty.Bench;

public static class Program
{
    private static readonly string[] AllProbeNames =
    [
        "conpty-roundtrip",
        "direct-pipe-roundtrip",
        "conpty-throughput-ascii",
        "conpty-throughput-sgr",
        "conpty-throughput-stress",
        "direct-pipe-throughput-ascii",
        "direct-pipe-throughput-sgr",
        "direct-pipe-throughput-stress",
    ];

    public static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage(Console.Error);
            return 64;
        }

        string probeName = args[0];
        string? outPath = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--out") outPath = args[i + 1];
        }

        if (!PreflightOk(out string preflightErr))
        {
            Console.Error.WriteLine(preflightErr);
            return 1;
        }

        // Wall-clock watchdog (spec Section 9.3). Synchronous blocking
        // reads in the harness cannot be interrupted by a CTS, so the
        // honest enforcement is a fire-and-forget Task that calls
        // Environment.Exit after the deadline. Per-probe budgets:
        //   round-trip: 30s (100 warmup + 1000 iterations)
        //   throughput: 60s (3 runs of 100 MB each)
        //   all:        10 minutes (8 probes in sequence)
        TimeSpan budget =
            probeName == "all"                 ? TimeSpan.FromMinutes(10) :
            probeName.Contains("roundtrip")    ? TimeSpan.FromSeconds(30) :
            probeName.Contains("throughput")   ? TimeSpan.FromSeconds(60) :
                                                 TimeSpan.FromSeconds(30);
        _ = Task.Run(async () =>
        {
            await Task.Delay(budget).ConfigureAwait(false);
            Console.Error.WriteLine($"watchdog: {probeName} exceeded {budget.TotalSeconds}s deadline");
            Environment.Exit(2);
        });

        try
        {
            if (probeName == "all")
            {
                return RunAll(outPath);
            }

            ResultJson? result = RunSingleByName(probeName);
            if (result is null)
            {
                Console.Error.WriteLine($"unknown probe: {probeName}");
                PrintUsage(Console.Error);
                return 64;
            }

            string json = ResultJson.SerializeIndented(result);
            if (outPath is null) Console.Out.WriteLine(json);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                File.WriteAllText(outPath, json);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("probe deadline exceeded");
            return 2;
        }
        catch (TransportException ex)
        {
            Console.Error.WriteLine($"transport error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unhandled: {ex}");
            return 1;
        }
    }

    private static ResultJson? RunSingleByName(string probeName)
    {
        string childExe = ResolveChildExe();
        HostInfo host = HostInfo.Capture();
        DateTime ts = DateTime.UtcNow;

        string transportLabel = probeName.StartsWith("conpty", StringComparison.Ordinal) ? "conpty" : "direct_pipe";

        using ITransport t = transportLabel switch
        {
            "conpty" => new ConPtyTransport(childExe),
            "direct_pipe" => new DirectPipeTransport(childExe),
            _ => throw new TransportException($"unknown transport label: {transportLabel}"),
        };

        return probeName switch
        {
            "conpty-roundtrip" => new RoundTripProbe("conpty_roundtrip", "conpty").Run(t, host, ts),
            "direct-pipe-roundtrip" => new RoundTripProbe("direct_pipe_roundtrip", "direct_pipe").Run(t, host, ts),
            "conpty-throughput-ascii" => new ThroughputProbe("conpty_throughput_ascii", "conpty", "ascii", Payloads.Ascii100Mb()).Run(t, host, ts),
            "conpty-throughput-sgr" => new ThroughputProbe("conpty_throughput_sgr", "conpty", "sgr", Payloads.Sgr100Mb()).Run(t, host, ts),
            "conpty-throughput-stress" => new ThroughputProbe("conpty_throughput_stress", "conpty", "stress", Payloads.Stress100Mb()).Run(t, host, ts),
            "direct-pipe-throughput-ascii" => new ThroughputProbe("direct_pipe_throughput_ascii", "direct_pipe", "ascii", Payloads.Ascii100Mb()).Run(t, host, ts),
            "direct-pipe-throughput-sgr" => new ThroughputProbe("direct_pipe_throughput_sgr", "direct_pipe", "sgr", Payloads.Sgr100Mb()).Run(t, host, ts),
            "direct-pipe-throughput-stress" => new ThroughputProbe("direct_pipe_throughput_stress", "direct_pipe", "stress", Payloads.Stress100Mb()).Run(t, host, ts),
            _ => null,
        };
    }

    private static int RunAll(string? outPath)
    {
        // Spawn one child process per probe for isolation: a handle
        // leak or hang in one probe cannot poison the next.
        string? selfPath = Environment.ProcessPath;
        if (selfPath is null)
        {
            Console.Error.WriteLine("cannot determine Environment.ProcessPath");
            return 1;
        }

        string dir = outPath ?? "results";
        Directory.CreateDirectory(dir);

        long overallStart = System.Diagnostics.Stopwatch.GetTimestamp();
        List<JsonElement> results = new();

        foreach (string probe in AllProbeNames)
        {
            string probeOut = Path.Combine(dir, $"{probe}.json");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = selfPath,
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(probe);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(probeOut);

            using var child = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("failed to spawn self for probe " + probe);
            child.WaitForExit();
            if (child.ExitCode != 0)
            {
                Console.Error.WriteLine($"{probe} exited with code {child.ExitCode}");
                Console.Error.WriteLine(child.StandardError.ReadToEnd());
                continue;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(probeOut));
            results.Add(doc.RootElement.Clone());
        }

        double elapsedSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - overallStart)
            / (double)System.Diagnostics.Stopwatch.Frequency;

        var summary = new
        {
            run_id = Guid.NewGuid().ToString(),
            duration_seconds = Math.Round(elapsedSeconds, 2),
            bench_version = ResultJson.CurrentVersion,
            host = HostInfo.Capture(),
            results,
        };

        File.WriteAllText(
            Path.Combine(dir, "summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static bool PreflightOk(out string err)
    {
        if (!OperatingSystem.IsWindows())
        {
            err = "error: Ghostty.Bench is Windows-only";
            return false;
        }
        var v = Environment.OSVersion.Version;
        if (v.Major < 10 || v.Build < 17763)
        {
            err = $"error: ConPTY requires Windows 10 1809 (build 17763) or newer; detected build {v.Build}";
            return false;
        }
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            err = "error: Ghostty.Bench PR 1 supports x64 only";
            return false;
        }
        err = "";
        return true;
    }

    private static string ResolveChildExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string candidate = Path.Combine(baseDir, "Ghostty.Bench.EchoChild.exe");
        if (!File.Exists(candidate))
        {
            throw new TransportException($"child binary not found at: {candidate}");
        }
        return candidate;
    }

    private static bool IsHelp(string s) =>
        s is "-h" or "--help" or "help";

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("usage: Ghostty.Bench <probe> [--out <path>]");
        w.WriteLine("probes:");
        foreach (var p in AllProbeNames) w.WriteLine($"  {p}");
        w.WriteLine("  all            -- run every probe, write results/<probe>.json + summary.json");
    }
}
