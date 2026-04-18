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
        //   throughput: 180s (3 iterations of 1 MB, per-iteration
        //                     deadline is 120s — three iterations cannot
        //                     usefully exceed 3x per-iteration budget, but
        //                     we leave slack for cold start + 60Hz conhost
        //                     refresh settling after burst completion)
        //   all:        10 minutes (8 probes in sequence)
        TimeSpan budget =
            probeName == "all"                 ? TimeSpan.FromMinutes(10) :
            probeName.Contains("roundtrip")    ? TimeSpan.FromSeconds(30) :
            probeName.Contains("throughput")   ? TimeSpan.FromSeconds(180) :
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

            if (probeName == "conpty-roundtrip-verify")
            {
                return RunConPtyVerify();
            }

            if (probeName == "conpty-throughput-verify")
            {
                // Optional payload size in KB: `conpty-throughput-verify 1024`
                int kb = 4;
                if (args.Length >= 2 && int.TryParse(args[1], out int parsed) && parsed > 0)
                    kb = parsed;
                return RunConPtyThroughputVerify(kb);
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

        // Drain any startup preamble (conhost VT preamble on ConPTY, no-op
        // on direct pipe) before the probe starts timing iterations. 2s is
        // ~10x a warm-machine conhost startup; longer means a broken spawn
        // or raw-mode activation, which deserves a fast distinct error.
        t.WaitReady(TimeSpan.FromSeconds(2));

        return probeName switch
        {
            "conpty-roundtrip" => new RoundTripProbe("conpty_roundtrip", "conpty").Run(t, host, ts),
            "direct-pipe-roundtrip" => new RoundTripProbe("direct_pipe_roundtrip", "direct_pipe").Run(t, host, ts),
            "conpty-throughput-ascii" => new ThroughputProbe("conpty_throughput_ascii", "conpty", "ascii", Payloads.Ascii1Mb()).Run(t, host, ts),
            "conpty-throughput-sgr" => new ThroughputProbe("conpty_throughput_sgr", "conpty", "sgr", Payloads.Sgr1Mb()).Run(t, host, ts),
            "conpty-throughput-stress" => new ThroughputProbe("conpty_throughput_stress", "conpty", "stress", Payloads.Stress1Mb()).Run(t, host, ts),
            "direct-pipe-throughput-ascii" => new ThroughputProbe("direct_pipe_throughput_ascii", "direct_pipe", "ascii", Payloads.Ascii1Mb()).Run(t, host, ts),
            "direct-pipe-throughput-sgr" => new ThroughputProbe("direct_pipe_throughput_sgr", "direct_pipe", "sgr", Payloads.Sgr1Mb()).Run(t, host, ts),
            "direct-pipe-throughput-stress" => new ThroughputProbe("direct_pipe_throughput_stress", "direct_pipe", "stress", Payloads.Stress1Mb()).Run(t, host, ts),
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
        w.WriteLine("  all                         -- run every probe, write results/<probe>.json + summary.json");
        w.WriteLine("  conpty-roundtrip-verify     -- 5-iteration diagnostic that proves conpty-roundtrip numbers are honest");
        w.WriteLine("  conpty-throughput-verify    -- small-payload diagnostic: shows what conhost emits for a single payload+terminator burst");
    }

    // Diagnostic variant of conpty-roundtrip. Runs 5 iterations with a
    // unique 3-byte payload per iteration ("!~A", "!~B", ..., "!~E"), so
    // each matched echo is provably from THIS iteration (a false match on
    // leftover bytes would show the wrong payload letter). Dumps per-
    // iteration: payload bytes written, every byte read until match, the
    // offset of the match, and the elapsed time. The goal is to show a
    // skeptical reader that the 15ms-level ConPTY round-trip timings from
    // conpty-roundtrip represent real byte-through-child round-trips,
    // not pipe-drain noise or VT-parameter false matches.
    private static int RunConPtyVerify()
    {
        string childExe = ResolveChildExe();
        using ITransport t = new ConPtyTransport(childExe);
        t.WaitReady(TimeSpan.FromSeconds(2));

        Console.Out.WriteLine("conpty-roundtrip-verify: 5 iterations with unique-per-iteration payloads");
        Console.Out.WriteLine("Payload shape: '!' '~' <letter>, where <letter> increments A..E per iteration.");
        Console.Out.WriteLine("A match of iteration N's payload proves the child received + echoed THIS iteration's bytes.");
        Console.Out.WriteLine();

        byte[] scratch = new byte[1024];
        long[] timings = new long[5];

        for (int i = 0; i < 5; i++)
        {
            byte letter = (byte)('A' + i);
            byte[] payload = [(byte)'!', (byte)'~', letter];

            Console.Out.WriteLine($"--- iteration {i} (letter '{(char)letter}') ---");
            Console.Out.Write("write: ");
            DumpBytes(Console.Out, payload);
            Console.Out.WriteLine();

            t.Input.Write(payload);
            t.Input.Flush();

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            long deadline = start + (long)(1.0 * System.Diagnostics.Stopwatch.Frequency);

            var window = new List<byte>(256);
            int readCalls = 0;
            int matchOffset = -1;

            while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
            {
                int n = t.Output.Read(scratch, 0, scratch.Length);
                readCalls++;
                if (n == 0) { Console.Out.WriteLine("  UNEXPECTED EOF"); return 1; }
                window.AddRange(scratch.AsSpan(0, n));

                int idx = CollectionsMarshal.AsSpan(window).IndexOf(payload.AsSpan());
                if (idx >= 0) { matchOffset = idx; break; }
            }

            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            double us = elapsed * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

            if (matchOffset < 0)
            {
                Console.Out.WriteLine($"  NO MATCH after {readCalls} read calls, window={window.Count} bytes:");
                DumpBytes(Console.Out, CollectionsMarshal.AsSpan(window));
                Console.Out.WriteLine();
                return 1;
            }

            Console.Out.Write($"read  ({readCalls} read call(s), {window.Count} bytes): ");
            DumpBytes(Console.Out, CollectionsMarshal.AsSpan(window));
            Console.Out.WriteLine();
            Console.Out.WriteLine($"match at offset {matchOffset} (payload bytes {(char)'!'}{(char)'~'}{(char)letter} = 0x21 0x7E 0x{letter:X2})");
            Console.Out.WriteLine($"elapsed: {us:F1} us ({elapsed} Stopwatch ticks)");
            Console.Out.WriteLine();

            timings[i] = elapsed;
        }

        Array.Sort(timings);
        long med = timings[timings.Length / 2];
        double medUs = med * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        Console.Out.WriteLine($"summary: median {medUs:F1} us, min {timings[0] * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency:F1} us, max {timings[4] * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency:F1} us");
        Console.Out.WriteLine("Each iteration found ITS OWN unique payload letter; leftover bytes from prior iterations would show the wrong letter.");

        return 0;
    }

    // Small-payload diagnostic for the throughput probe protocol. Writes a
    // 4 KB ASCII payload + the same "\r\n~ENDOFBURST_<nonce>~" terminator the
    // real throughput probe uses, then reads conhost's hOutput for up to
    // 10 s, reporting:
    //   - total bytes read
    //   - whether the terminator appears anywhere in the accumulated stream
    //   - hexdump-style rendering of the first 256 and last 512 bytes
    // The goal is to see whether conhost emits the terminator at all, and
    // if so, whether it is byte-contiguous or interrupted by VT framing
    // (cursor-move / erase-in-line / SGR sequences mid-row).
    private static int RunConPtyThroughputVerify(int payloadKb)
    {
        string childExe = ResolveChildExe();
        using ITransport t = new ConPtyTransport(childExe);
        t.WaitReady(TimeSpan.FromSeconds(2));

        string nonce = Guid.NewGuid().ToString("N").Substring(0, 16);
        byte[] terminator = System.Text.Encoding.ASCII.GetBytes(
            "\r\n~ENDOFBURST_" + nonce + "~");
        byte[] payload = new byte[payloadKb * 1024];
        Array.Fill(payload, (byte)'A');

        // Read window scales with payload: 2 s baseline + 2 s per 100 KB
        // extrapolated from the 4 KB = ~80 ms observation, with a cap.
        int readSeconds = Math.Min(180, 2 + payloadKb / 50);

        Console.Out.WriteLine($"conpty-throughput-verify: {payloadKb} KB ASCII + terminator, {readSeconds} s read window");
        Console.Out.WriteLine($"payload: {payload.Length} bytes of 'A'");
        Console.Out.Write("terminator bytes: ");
        DumpBytes(Console.Out, terminator);
        Console.Out.WriteLine();
        Console.Out.WriteLine();

        // Parallel reader: start draining hOutput BEFORE writing the payload
        // so conhost's emit side never backpressures (which, sequentially,
        // deadlocks at >4 KB because the hOutput pipe fills up).
        byte[] scratch = new byte[64 * 1024];
        var all = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        long totalRead = 0;
        int readCalls = 0;
        var found = new ManualResetEventSlim(false);
        int foundOffset = -1;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(readSeconds));

        var reader = Task.Run(async () =>
        {
            // Simple accumulator that scans after each read. Uses the full
            // accumulated buffer for scanning so cross-read matches work
            // without implementing the production probe's carryover trick.
            var accum = new List<byte>(payloadKb * 1024);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    int n = await t.Output.ReadAsync(scratch.AsMemory(0, scratch.Length), cts.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    Interlocked.Add(ref totalRead, n);
                    Interlocked.Increment(ref readCalls);
                    for (int j = 0; j < n; j++) accum.Add(scratch[j]);

                    int hit = CollectionsMarshal.AsSpan(accum).IndexOf(terminator.AsSpan());
                    if (hit >= 0)
                    {
                        foundOffset = hit;
                        all.Enqueue(accum.ToArray());
                        found.Set();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            all.Enqueue(accum.ToArray());
        }, cts.Token);

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        t.Input.Write(payload);
        t.Input.Write(terminator);
        t.Input.Flush();

        // Wait for either the terminator to appear or the deadline to fire.
        bool matched = found.Wait(TimeSpan.FromSeconds(readSeconds));
        cts.Cancel();
        try { reader.Wait(TimeSpan.FromSeconds(2)); } catch { }

        // Reassemble accumulated bytes (single item in queue).
        byte[] accumBytes = all.TryDequeue(out var first) ? first : Array.Empty<byte>();

        if (matched && foundOffset >= 0)
        {
            double us = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.Out.WriteLine($"TERMINATOR FOUND at offset {foundOffset} after {readCalls} read call(s), {accumBytes.Length} total bytes, {us:F0} us");
            Console.Out.WriteLine();
            DumpThroughputVerifySlicesArr(accumBytes);
            return 0;
        }

        double elapsedUs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        Console.Out.WriteLine($"TERMINATOR NOT FOUND after {readCalls} read call(s), {accumBytes.Length} total bytes, {elapsedUs:F0} us");
        Console.Out.WriteLine();
        DumpThroughputVerifySlicesArr(accumBytes);

        byte[] prefix = System.Text.Encoding.ASCII.GetBytes("~ENDOFBURST_");
        int prefixIdx = accumBytes.AsSpan().IndexOf(prefix.AsSpan());
        if (prefixIdx >= 0)
        {
            Console.Out.WriteLine($"DIAGNOSIS: ~ENDOFBURST_ prefix DOES appear at offset {prefixIdx}; the 16-char nonce or trailing '~' must be split by VT framing. Next 64 bytes from that offset:");
            int len = Math.Min(64, accumBytes.Length - prefixIdx);
            DumpBytes(Console.Out, accumBytes.AsSpan(prefixIdx, len));
            Console.Out.WriteLine();
        }
        else
        {
            Console.Out.WriteLine("DIAGNOSIS: the literal substring '~ENDOFBURST_' does NOT appear anywhere in what conhost emitted. The terminator is either not reaching hOutput or being mangled beyond substring match.");
        }

        return 1;
    }

    private static void DumpThroughputVerifySlicesArr(byte[] all)
    {
        int firstLen = Math.Min(256, all.Length);
        Console.Out.WriteLine($"FIRST {firstLen} BYTES of emission:");
        DumpBytes(Console.Out, all.AsSpan(0, firstLen));
        Console.Out.WriteLine();
        Console.Out.WriteLine();

        int lastLen = Math.Min(512, all.Length);
        int lastStart = all.Length - lastLen;
        Console.Out.WriteLine($"LAST {lastLen} BYTES of emission (offset {lastStart}..{all.Length}):");
        DumpBytes(Console.Out, all.AsSpan(lastStart, lastLen));
        Console.Out.WriteLine();
        Console.Out.WriteLine();
    }

    // Compact per-byte dump: printable ASCII as the char, else \xNN.
    private static void DumpBytes(TextWriter w, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b >= 0x20 && b < 0x7F) w.Write((char)b);
            else w.Write($"\\x{b:X2}");
        }
    }
}
