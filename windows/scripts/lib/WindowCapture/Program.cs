using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WindowCapture;

/// <summary>
/// Film one window at the rate the compositor actually presents it.
///
/// Why this exists: the harnesses in this repo used
/// Graphics.CopyFromScreen, and on the machine this was written for that
/// costs about 175ms per grab REGARDLESS of region size -- measured
/// identically at 1280x820, 640x820, 1280x200 and 400x400, and unchanged by
/// CAPTUREBLT. Five frames a second cannot judge a 340ms animation; it can
/// barely give you the start and the end, which is the before/after pair a
/// filmstrip exists to replace.
///
/// Windows.Graphics.Capture takes frames from the compositor rather than
/// reading back a composited desktop, and -- the property that matters most
/// here -- CreateFreeThreaded delivers them on a pool thread. The subject of
/// these measurements blocks its own UI thread for a few hundred
/// milliseconds at a time, so any instrument that needs that thread is
/// looking through the very stall it is trying to observe.
///
/// OUT OF PROCESS on purpose. An in-process capture would put the frame pool
/// inside the process whose thread is jammed and would perturb what it
/// measures. This is a separate exe pointed at an HWND, so the observer
/// stays off the observed.
/// </summary>
internal static class Program
{
    private sealed record Shot(SoftwareBitmap Bitmap, double AtMs);

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"CAPTURE_FAIL {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        var hwnd = (nint)ArgLong(args, "--hwnd", 0);
        var durationMs = (int)ArgLong(args, "--ms", 1500);
        var maxFrames = (int)ArgLong(args, "--max-frames", 240);
        var outDir = ArgString(args, "--out") ?? "";
        var tag = ArgString(args, "--tag") ?? "frame";
        var probe = args.Contains("--probe");

        if (hwnd == 0) throw new ArgumentException("--hwnd is required");
        if (!probe && outDir.Length == 0)
            throw new ArgumentException("--out is required unless --probe");
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows.Graphics.Capture is not supported here");

        var device = Interop.CreateDirect3DDevice();
        var item = Interop.CreateItemForWindow(hwnd);
        var size = item.Size;

        // Bounded so a slow encoder cannot turn a long capture into an OOM.
        // A dropped frame is reported rather than silently smoothed over: a
        // filmstrip with holes in it that does not say so is worse than one
        // that does.
        var queue = new BlockingCollection<Shot>(boundedCapacity: 64);
        var dropped = 0;
        var arrived = 0;
        var copyTicks = 0L;
        var clock = Stopwatch.StartNew();

        // Two buffers is the documented minimum for a free-threaded pool and
        // is what keeps latency down; more only lets the compositor run
        // further ahead of a consumer that is already keeping up.
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);

        pool.FrameArrived += (sender, _) =>
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            var at = clock.Elapsed.TotalMilliseconds;
            Interlocked.Increment(ref arrived);

            var t0 = Stopwatch.GetTimestamp();
            // The whole reason there is no D3D11 staging texture in this
            // file: CreateCopyFromSurfaceAsync is the projected route from a
            // capture surface to CPU pixels. Awaited synchronously because
            // this callback is already on a pool thread with nothing else to
            // do, and keeping frames in order matters more than overlapping
            // their copies.
            SoftwareBitmap bitmap;
            try
            {
                bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(
                    frame.Surface, BitmapAlphaMode.Premultiplied).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // A frame whose surface went away mid-copy is a dropped
                // frame, not a failed run.
                Interlocked.Increment(ref dropped);
                return;
            }
            Interlocked.Add(ref copyTicks, Stopwatch.GetTimestamp() - t0);

            if (!queue.TryAdd(new Shot(bitmap, at)))
            {
                Interlocked.Increment(ref dropped);
                bitmap.Dispose();
            }
        };

        using var session = pool.CreateCaptureSession(item);
        // The cursor is not part of the app and would land in the middle of
        // whatever the mouse happens to be over.
        try { session.IsCursorCaptureEnabled = false; } catch (Exception) { }
        // The yellow capture border draws INSIDE the captured frame, so it
        // would sit on top of the chrome these films are taken to judge.
        // Neither route is required: if the border survives, the summary
        // says so and the frames are still usable.
        var borderless = TryGoBorderless(session);

        var encoded = new List<object>();
        var encoder = Task.Run(() =>
        {
            foreach (var shot in queue.GetConsumingEnumerable())
            {
                using var bitmap = shot.Bitmap;
                if (probe) continue;
                var index = encoded.Count;
                var name = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}-{1:d3}-t{2:d4}ms.png", tag, index, (int)shot.AtMs);
                WritePng(bitmap, Path.Combine(outDir, name));
                encoded.Add(new
                {
                    i = index,
                    atMs = Math.Round(shot.AtMs, 2),
                    file = name,
                    w = bitmap.PixelWidth,
                    h = bitmap.PixelHeight,
                });
            }
        });

        session.StartCapture();
        // The harness must not fire the thing it wants filmed before the
        // camera is rolling.
        //
        // The number is this tool's own clock at the instant capture
        // started, and it is what makes a film correlatable with anything
        // else. Frame timestamps are on this clock, which starts before
        // the frame pool exists; a caller's clock starts when it gets this
        // line. Without the offset the two are out by a hundred
        // milliseconds or so, which on a 340ms animation is the
        // difference between reading the right frame and the wrong one --
        // and the error is invisible, because the wrong frame is still a
        // plausible picture of a transition.
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "READY {0:F1}", clock.Elapsed.TotalMilliseconds));
        Console.Out.Flush();

        var deadline = clock.Elapsed.TotalMilliseconds + durationMs;
        while (clock.Elapsed.TotalMilliseconds < deadline
               && Volatile.Read(ref arrived) < maxFrames)
        {
            Thread.Sleep(5);
        }

        var elapsed = clock.Elapsed.TotalSeconds;
        session.Dispose();
        pool.Dispose();
        queue.CompleteAdding();
        encoder.Wait();

        var frames = Volatile.Read(ref arrived);
        var meanCopyMs = frames == 0
            ? 0
            : (Volatile.Read(ref copyTicks) / (double)Stopwatch.Frequency) * 1000.0 / frames;

        if (!probe && outDir.Length > 0)
        {
            File.WriteAllText(
                Path.Combine(outDir, tag + "-index.json"),
                JsonSerializer.Serialize(encoded), Encoding.UTF8);
        }

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "SUMMARY size={0}x{1} frames={2} dropped={3} seconds={4:F3} fps={5:F1} meanCopyMs={6:F2} borderless={7}",
            size.Width, size.Height, frames, Volatile.Read(ref dropped),
            elapsed, frames / Math.Max(elapsed, 0.001), meanCopyMs, borderless));
        return 0;
    }

    private static bool TryGoBorderless(GraphicsCaptureSession session)
    {
        try
        {
            // Newer builds gate the property behind an access request; older
            // ones have neither and throw on the property itself.
            _ = GraphicsCaptureAccess.RequestAccessAsync(
                GraphicsCaptureAccessKind.Borderless).GetAwaiter().GetResult();
        }
        catch (Exception) { }
        try
        {
            session.IsBorderRequired = false;
            return !session.IsBorderRequired;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Encode through WinRT rather than System.Drawing: the TFM already
    /// carries BitmapEncoder, and System.Drawing.Common would be a NuGet
    /// dependency for a tool whose entire appeal is not having one.
    /// </summary>
    private static void WritePng(SoftwareBitmap bitmap, string path)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .GetAwaiter().GetResult();
        encoder.SetSoftwareBitmap(bitmap);
        encoder.FlushAsync().GetAwaiter().GetResult();

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        reader.LoadAsync((uint)stream.Size).GetAwaiter().GetResult();
        reader.ReadBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private static string? ArgString(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static long ArgLong(string[] args, string name, long fallback)
        => long.TryParse(ArgString(args, name), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
