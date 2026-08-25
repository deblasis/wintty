using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Ghostty.Core.Clipboard;
using Ghostty.Core.Interop;
using Xunit;

namespace Ghostty.Tests.Clipboard;

/// <summary>
/// Randomized round-trip tests over the clipboard marshalling boundary.
///
/// The oracle is round-trip fidelity, not "it did not crash". Every case
/// builds real unmanaged memory with the writer, reads it back with the
/// reader, and asserts the bytes that come out are the bytes that went in.
/// That is what makes this worth running: a stride, offset or length bug
/// is precisely a bug that survives a liveness check and corrupts the
/// payload instead.
///
/// Inputs are adversarial but VALID. The struct contract says `len` bounds
/// a buffer that really is that long, so feeding a length past the end of
/// an allocation would not be finding a defect, it would be inventing
/// undefined behaviour and blaming the code for it. What is randomized is
/// everything the contract does allow: empty payloads, embedded NULs,
/// non-UTF8 bytes, multi-byte and supplementary-plane MIME names, entry
/// counts, and payload sizes across the small/large boundary.
///
/// Seeds are explicit and the failure message carries the seed and the
/// iteration, so any finding replays exactly. Iteration count comes from
/// GHOSTTY_FUZZ_ITERATIONS so the ladder can run a cheap pass and
/// `just clipboard-fuzz` can run a deep one against the same code.
/// </summary>
public sealed class ClipboardMarshallingFuzzTests
{

    // Seeds are fixed and spelled out at each theory below, so the ladder
    // run is deterministic. A fuzzer that picks a fresh seed every run turns
    // a real defect into a flake, and a flake into something people rerun
    // until it passes.

    private static int Iterations
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("GHOSTTY_FUZZ_ITERATIONS");
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : 200;
        }
    }

    // --- generators -----------------------------------------------------

    private static readonly string[] MimePool =
    {
        ClipboardMime.TextPlain,
        ClipboardMime.TextHtml,
        ClipboardMime.TextUriList,
        ClipboardMime.ImagePng,
        "application/x-unknown",
        "text/plain;charset=utf-8",
        // Non-ASCII and supplementary plane. The MIME name is a C string on
        // the wire, so its UTF-8 length differs from its UTF-16 length, and
        // getting that wrong truncates the name.
        "text/\u00e9\u00e8\u00ea",
        "text/\U0001F4CB",
        "",
    };

    private static byte[] RandomData(Random rng)
    {
        // Bucketed rather than uniform: the interesting sizes cluster at the
        // boundaries, and a uniform draw over a wide range almost never
        // produces one.
        var bucket = rng.Next(6);
        var len = bucket switch
        {
            0 => 0,
            1 => 1,
            2 => rng.Next(2, 32),
            3 => rng.Next(32, 512),
            4 => rng.Next(4096, 8192),
            _ => rng.Next(0, 64),
        };

        var data = new byte[len];
        rng.NextBytes(data);

        // Force NULs in about a third of cases. This is the byte that a
        // strlen-based reader stops at, so it has to appear far more often
        // than chance would put it in a short buffer.
        if (len > 2 && rng.Next(3) == 0)
        {
            data[rng.Next(len)] = 0;
            data[0] = 0;
        }

        return data;
    }

    private static List<ClipboardPayload> RandomPayloads(Random rng)
    {
        var count = rng.Next(4) switch
        {
            0 => 0,
            1 => 1,
            2 => rng.Next(2, 5),
            _ => rng.Next(1, 3),
        };

        var payloads = new List<ClipboardPayload>(count);
        for (var i = 0; i < count; i++)
            payloads.Add(new ClipboardPayload(MimePool[rng.Next(MimePool.Length)], RandomData(rng)));

        return payloads;
    }

    private static List<string> RandomAvailable(Random rng)
    {
        var count = rng.Next(0, 5);
        var available = new List<string>(count);
        for (var i = 0; i < count; i++)
            available.Add(MimePool[rng.Next(MimePool.Length)]);
        return available;
    }

    // --- oracle 1: contents survive the round trip ----------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(21)]
    [InlineData(34)]
    public void Contents_RoundTrip_Exactly(int seed)
    {
        var rng = new Random(seed);

        for (var i = 0; i < Iterations; i++)
        {
            var payloads = RandomPayloads(rng);
            var available = RandomAvailable(rng);
            var confirmed = rng.Next(2) == 0;
            var remember = rng.Next(2) == 0;

            using var native = new NativeClipboardComplete(payloads, available, confirmed, remember);

            var contentsPtr = Marshal.ReadIntPtr(native.Pointer, GhosttyClipboardLayout.CompleteContents);
            var contentsLen = (nuint)(nint)Marshal.ReadIntPtr(
                native.Pointer, GhosttyClipboardLayout.CompleteContentsLen);

            var readBack = ClipboardContentMarshaller.Read(contentsPtr, contentsLen);

            var because = $"seed={seed} iteration={i} count={payloads.Count}";
            Assert.True(payloads.Count == readBack.Count,
                $"entry count changed: {because} expected={payloads.Count} actual={readBack.Count}");

            for (var k = 0; k < payloads.Count; k++)
            {
                Assert.True(payloads[k].Mime == readBack[k].Mime,
                    $"mime changed at {k}: {because} expected={Describe(payloads[k].Mime)} actual={Describe(readBack[k].Mime)}");
                Assert.True(payloads[k].Data.Span.SequenceEqual(readBack[k].Data.Span),
                    $"data changed at {k}: {because} expected={Hex(payloads[k].Data.Span)} actual={Hex(readBack[k].Data.Span)}");
            }
        }

    }

    // --- oracle 2: the flags and the available list survive too ---------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(21)]
    [InlineData(34)]
    public void CompleteFlagsAndAvailable_RoundTrip(int seed)
    {
        var rng = new Random(seed);

        for (var i = 0; i < Iterations; i++)
        {
            var payloads = RandomPayloads(rng);
            var available = RandomAvailable(rng);
            var confirmed = rng.Next(2) == 0;
            var remember = rng.Next(2) == 0;

            using var native = new NativeClipboardComplete(payloads, available, confirmed, remember);
            var because = $"seed={seed} iteration={i}";

            // The two bools sit adjacent at the tail of the struct, which is
            // exactly where an off-by-one offset reads the wrong one and
            // still returns a plausible 0 or 1.
            Assert.True(
                (confirmed ? 1 : 0) == Marshal.ReadByte(native.Pointer, GhosttyClipboardLayout.CompleteConfirmed),
                $"confirmed flag wrong: {because}");
            Assert.True(
                (remember ? 1 : 0) == Marshal.ReadByte(native.Pointer, GhosttyClipboardLayout.CompleteRemember),
                $"remember flag wrong: {because}");

            var availablePtr = Marshal.ReadIntPtr(native.Pointer, GhosttyClipboardLayout.CompleteAvailable);
            var availableLen = (nuint)(nint)Marshal.ReadIntPtr(
                native.Pointer, GhosttyClipboardLayout.CompleteAvailableLen);

            var readBack = ClipboardConfirmMarshaller.ReadStringArray(availablePtr, availableLen);

            // Empty MIME names are dropped by the reader on purpose, so the
            // expectation filters them rather than asserting a raw equality
            // that would fail for the right reason and look like the wrong one.
            var expected = available.Where(a => !string.IsNullOrEmpty(a)).ToList();
            Assert.True(expected.SequenceEqual(readBack),
                $"available list changed: {because} expected=[{string.Join("|", expected)}] actual=[{string.Join("|", readBack)}]");
        }
    }

    // --- oracle 3: the confirm payload survives -------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(21)]
    [InlineData(34)]
    public void ConfirmSnapshot_RoundTrips(int seed)
    {
        var rng = new Random(seed);

        for (var i = 0; i < Iterations; i++)
        {
            var payloads = RandomPayloads(rng);
            var available = RandomAvailable(rng);
            var name = rng.Next(3) == 0 ? null : $"session-{rng.Next(1000)}";
            var canRemember = rng.Next(2) == 0;

            using var scratch = new ConfirmScratch(payloads, available, name, canRemember);
            var snapshot = ClipboardConfirmMarshaller.Read(scratch.Pointer);
            var because = $"seed={seed} iteration={i}";

            Assert.True(payloads.Count == snapshot.Contents.Count,
                $"contents count changed: {because}");
            for (var k = 0; k < payloads.Count; k++)
            {
                Assert.True(payloads[k].Data.Span.SequenceEqual(snapshot.Contents[k].Data.Span),
                    $"confirm data changed at {k}: {because}");
            }

            Assert.True(name == snapshot.Name, $"name changed: {because}");
            Assert.True(canRemember == snapshot.CanRemember, $"can_remember changed: {because}");

            // PreviewText must pick a text/* entry, never simply the first.
            var expectedPreview = payloads
                .FirstOrDefault(p => p.Mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
            if (expectedPreview.Mime is not null)
            {
                Assert.True(expectedPreview.Text == snapshot.PreviewText,
                    $"preview picked the wrong entry: {because}");
            }
            else
            {
                Assert.True(snapshot.PreviewText.Length == 0,
                    $"preview should be empty with no text entry: {because}");
            }
        }
    }

    /// <summary>
    /// Builds an unmanaged ghostty_clipboard_confirm_s. Shares the content
    /// array builder with NativeClipboardComplete via that type, so only
    /// the confirm-specific tail is hand-written here.
    /// </summary>
    private sealed class ConfirmScratch : IDisposable
    {
        private readonly NativeClipboardComplete _borrowed;
        private readonly IntPtr _struct;
        private readonly IntPtr _name;

        public ConfirmScratch(
            IReadOnlyList<ClipboardPayload> contents,
            IReadOnlyList<string> available,
            string? name,
            bool canRemember)
        {
            _borrowed = new NativeClipboardComplete(contents, available, false, false);
            _struct = Marshal.AllocHGlobal(GhosttyClipboardLayout.ConfirmSize);

            _name = IntPtr.Zero;
            if (name is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(name);
                _name = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, _name, bytes.Length);
                Marshal.WriteByte(_name, bytes.Length, 0);
            }

            Marshal.WriteIntPtr(_struct, GhosttyClipboardLayout.ConfirmContents,
                Marshal.ReadIntPtr(_borrowed.Pointer, GhosttyClipboardLayout.CompleteContents));
            Marshal.WriteIntPtr(_struct, GhosttyClipboardLayout.ConfirmContentsLen,
                Marshal.ReadIntPtr(_borrowed.Pointer, GhosttyClipboardLayout.CompleteContentsLen));
            Marshal.WriteIntPtr(_struct, GhosttyClipboardLayout.ConfirmAvailable,
                Marshal.ReadIntPtr(_borrowed.Pointer, GhosttyClipboardLayout.CompleteAvailable));
            Marshal.WriteIntPtr(_struct, GhosttyClipboardLayout.ConfirmAvailableLen,
                Marshal.ReadIntPtr(_borrowed.Pointer, GhosttyClipboardLayout.CompleteAvailableLen));
            Marshal.WriteIntPtr(_struct, GhosttyClipboardLayout.ConfirmName, _name);
            Marshal.WriteByte(_struct, GhosttyClipboardLayout.ConfirmCanRemember, canRemember ? (byte)1 : (byte)0);
        }

        public IntPtr Pointer => _struct;

        public void Dispose()
        {
            if (_name != IntPtr.Zero) Marshal.FreeHGlobal(_name);
            Marshal.FreeHGlobal(_struct);
            _borrowed.Dispose();
        }
    }

    // --- oracle 4: uri-list round trips back to the path ----------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(21)]
    [InlineData(34)]
    public void UriList_RoundTripsToTheOriginalPaths(int seed)
    {
        var rng = new Random(seed);

        // Characters that are legal in a Windows filename and awkward in a
        // URI. The point of the formatter is that these survive.
        var fragments = new[]
        {
            "plain", "with space", "with#hash", "with%percent", "with&amp",
            "with+plus", "with,comma", "with=equals", "with@at", "with[bracket]",
            "\u00e9\u00e8", "\u65e5\u672c\u8a9e", "dotted.name", "'apostrophe",
        };

        for (var i = 0; i < Iterations; i++)
        {
            var count = rng.Next(1, 5);
            var paths = new List<string>(count);
            for (var k = 0; k < count; k++)
            {
                var depth = rng.Next(1, 4);
                var parts = new List<string>();
                for (var d = 0; d < depth; d++) parts.Add(fragments[rng.Next(fragments.Length)]);
                paths.Add(@"C:\" + string.Join(@"\", parts) + ".txt");
            }

            var body = UriListFormatter.Format(paths);
            var because = $"seed={seed} iteration={i} paths=[{string.Join("|", paths)}]";

            Assert.True(body is not null, $"formatter returned null for usable paths: {because}");

            var lines = body!.Split("\r\n");
            Assert.True(paths.Count == lines.Length,
                $"line count changed: {because} expected={paths.Count} actual={lines.Length}");

            for (var k = 0; k < paths.Count; k++)
            {
                // The real oracle: parse the emitted URI back and compare the
                // local path. Asserting on the escaped text would only restate
                // the implementation.
                var uri = new Uri(lines[k]);
                Assert.True(uri.IsFile, $"line {k} is not a file URI: {because} line={lines[k]}");
                Assert.True(
                    string.Equals(paths[k], uri.LocalPath, StringComparison.OrdinalIgnoreCase),
                    $"path did not survive at {k}: {because} expected={paths[k]} actual={uri.LocalPath}");
            }
        }
    }

    // --- helpers --------------------------------------------------------

    private static string Describe(string? s) =>
        s is null ? "<null>" : $"\"{s}\" ({Encoding.UTF8.GetByteCount(s)}B)";

    private static string Hex(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return "<empty>";
        var take = Math.Min(data.Length, 16);
        var sb = new StringBuilder($"[{data.Length}B] ");
        for (var i = 0; i < take; i++) sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
        if (take < data.Length) sb.Append("...");
        return sb.ToString();
    }
}
