using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Ghostty.Core.Logging;
using Ghostty.Core.Logging.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ghostty.Tests.Logging;

/// <summary>
/// Unit tests for the libghostty log bridge. We drive the bridge with a
/// fake installer that captures the registered native function pointer,
/// invoke that pointer directly from managed code (simulating the Zig
/// call site), and assert the resulting ILogger emission.
/// </summary>
public class LibghosttyLogBridgeTests
{
    [Fact]
    public void MapLevel_MatchesZigSideContract()
    {
        Assert.Equal(LogLevel.Debug, LibghosttyLogBridge.MapLevel(0));
        Assert.Equal(LogLevel.Information, LibghosttyLogBridge.MapLevel(1));
        Assert.Equal(LogLevel.Warning, LibghosttyLogBridge.MapLevel(2));
        Assert.Equal(LogLevel.Error, LibghosttyLogBridge.MapLevel(3));
    }

    [Fact]
    public void MapLevel_UnknownValueFallsBackToInformation()
    {
        // Future Zig-side additions must not disappear silently.
        Assert.Equal(LogLevel.Information, LibghosttyLogBridge.MapLevel(42));
        Assert.Equal(LogLevel.Information, LibghosttyLogBridge.MapLevel(uint.MaxValue));
    }

    [Fact]
    public void ReadUtf8_NullPointerProducesEmptyString()
    {
        Assert.Equal(string.Empty, LibghosttyLogBridge.ReadUtf8(IntPtr.Zero, 10));
    }

    [Fact]
    public void ReadUtf8_ZeroLengthProducesEmptyString()
    {
        // Use any non-null pointer; with len=0 it must not be read.
        var sentinel = new byte[] { 0xFF };
        var handle = GCHandle.Alloc(sentinel, GCHandleType.Pinned);
        try
        {
            Assert.Equal(string.Empty,
                LibghosttyLogBridge.ReadUtf8(handle.AddrOfPinnedObject(), 0));
        }
        finally { handle.Free(); }
    }

    [Fact]
    public void ReadUtf8_DecodesMultibyteUtf8()
    {
        // Include a 3-byte UTF-8 codepoint (U+2192 RIGHTWARDS ARROW,
        // bytes E2 86 92) so the test covers non-ASCII decoding rather
        // than just ASCII pass-through. Built from bytes so this source
        // file stays ASCII-only.
        var payload = new byte[] {
            (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o',
            (byte)' ', 0xE2, 0x86, 0x92, (byte)' ',
            (byte)'w', (byte)'o', (byte)'r', (byte)'l', (byte)'d',
        };
        var handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            var s = LibghosttyLogBridge.ReadUtf8(handle.AddrOfPinnedObject(), payload.Length);
            Assert.Equal(15, payload.Length);
            // Expected: "hello \u2192 world"
            Assert.Equal("hello \u2192 world", s);
        }
        finally { handle.Free(); }
    }

    [Fact]
    public void ReadCategory_EmptyScopeMapsToDefaultCategory()
    {
        Assert.Equal(LibghosttyLogBridge.DefaultCategory,
            LibghosttyLogBridge.ReadCategory(IntPtr.Zero, 0));
    }

    [Fact]
    public void ReadCategory_NamedScopeIsPrefixed()
    {
        var payload = Encoding.UTF8.GetBytes("termio");
        var handle = GCHandle.Alloc(payload, GCHandleType.Pinned);
        try
        {
            var category = LibghosttyLogBridge.ReadCategory(
                handle.AddrOfPinnedObject(), payload.Length);
            Assert.Equal("Ghostty.Zig.termio", category);
        }
        finally { handle.Free(); }
    }

    [Fact]
    public void Install_RoutesCallbackIntoLoggerFactory()
    {
        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var installer = new FakeInstaller();
        using var bridge = new LibghosttyLogBridge(factory, installer);
        bridge.Install();

        Assert.NotEqual(IntPtr.Zero, installer.LastCallback);

        // Simulate a Zig-side log call.
        InvokeNativeCallback(
            installer.LastCallback,
            level: 1,
            scope: "termio",
            message: "conpty-mode=raw verdict={d}");

        var entry = Assert.Single(capture.Entries);
        Assert.Equal("Ghostty.Zig.termio", entry.Category);
        Assert.Equal(LogLevel.Information, entry.Level);
        // The bridge passes the message through verbatim; no format
        // placeholder substitution happens here (Zig already rendered it).
        Assert.Equal("conpty-mode=raw verdict={d}", entry.Message);
    }

    [Fact]
    public void Dispose_ClearsNativeCallback()
    {
        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var installer = new FakeInstaller();
        var bridge = new LibghosttyLogBridge(factory, installer);
        bridge.Install();
        Assert.NotEqual(IntPtr.Zero, installer.LastCallback);

        bridge.Dispose();

        // Dispose must call SetCallback(null, null) so libghostty stops
        // emitting into a factory we are about to tear down.
        Assert.Equal(IntPtr.Zero, installer.LastCallback);
        Assert.Equal(IntPtr.Zero, installer.LastUserData);
    }

    [Fact]
    public void InvokingDisposedBridgeCallback_IsSilentlyIgnored()
    {
        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var installer = new FakeInstaller();
        var bridge = new LibghosttyLogBridge(factory, installer);
        bridge.Install();
        // Capture the native callback pointer BEFORE disposal so we
        // can simulate a late Zig thread that latched it then slept.
        var latePtr = installer.LastCallback;

        bridge.Dispose();

        // Invoking the callback post-dispose must not throw and must
        // not produce an emission: the bridge's internal _disposed
        // flag short-circuits the OnLog body.
        InvokeNativeCallback(latePtr, level: 2, scope: "late", message: "after-dispose");

        Assert.Empty(capture.Entries);
    }

    [Fact]
    public void UnicodeMessage_PassesThroughIntact()
    {
        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var installer = new FakeInstaller();
        using var bridge = new LibghosttyLogBridge(factory, installer);
        bridge.Install();

        // Path with non-ASCII bytes, simulating a Windows shell arg.
        InvokeNativeCallback(
            installer.LastCallback,
            level: 3,
            scope: "shell",
            message: "spawn cwd=C:\\Users\\Mocha\\Documents");

        var entry = Assert.Single(capture.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Ghostty.Zig.shell", entry.Category);
        Assert.Equal("spawn cwd=C:\\Users\\Mocha\\Documents", entry.Message);
    }

    [Fact]
    public void DefaultScope_MapsToBareCategory()
    {
        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var installer = new FakeInstaller();
        using var bridge = new LibghosttyLogBridge(factory, installer);
        bridge.Install();

        // Empty scope (Zig passes a zero-length slice for the default
        // scope) must resolve to "Ghostty.Zig" without a trailing dot.
        InvokeNativeCallback(
            installer.LastCallback,
            level: 1,
            scope: string.Empty,
            message: "no-scope");

        var entry = Assert.Single(capture.Entries);
        Assert.Equal("Ghostty.Zig", entry.Category);
    }

    [Fact]
    public void ConcurrentInvocations_AllLandInCapture()
    {
        // Exercises the ConcurrentDictionary<string, ILogger> cache and
        // the _disposed Interlocked guard under contention. The Zig
        // side calls OnLog from any thread, so this mirrors production.
        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var installer = new FakeInstaller();
        using var bridge = new LibghosttyLogBridge(factory, installer);
        bridge.Install();

        const int threads = 8;
        const int perThread = 128;

        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                InvokeNativeCallback(
                    installer.LastCallback,
                    level: 1,
                    scope: "worker" + (t % 4),
                    message: "msg" + i);
            }
        });

        Assert.Equal(threads * perThread, capture.Entries.Count);
    }

    // --- helpers -----------------------------------------------------

    // Simulate libghostty calling the registered callback. Pins the
    // UTF-8 bytes for scope and message for the duration of the call,
    // then invokes the delegate via its captured function pointer.
    private static void InvokeNativeCallback(
        IntPtr fnPtr,
        uint level,
        string scope,
        string message)
    {
        // Resurrect the managed delegate from the native pointer so we
        // can invoke through the managed path in tests.
        var cb = Marshal.GetDelegateForFunctionPointer<LibghosttyLogBridge.LogCallbackDelegate>(fnPtr);

        var scopeBytes = Encoding.UTF8.GetBytes(scope);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        var scopeHandle = GCHandle.Alloc(scopeBytes, GCHandleType.Pinned);
        var messageHandle = GCHandle.Alloc(messageBytes, GCHandleType.Pinned);
        try
        {
            cb(
                level,
                scopeBytes.Length == 0 ? IntPtr.Zero : scopeHandle.AddrOfPinnedObject(),
                (UIntPtr)scopeBytes.Length,
                messageBytes.Length == 0 ? IntPtr.Zero : messageHandle.AddrOfPinnedObject(),
                (UIntPtr)messageBytes.Length,
                IntPtr.Zero);
        }
        finally
        {
            scopeHandle.Free();
            messageHandle.Free();
        }
    }

    private sealed class FakeInstaller : LibghosttyLogBridge.INativeInstaller
    {
        public IntPtr LastCallback { get; private set; }
        public IntPtr LastUserData { get; private set; }

        public void SetCallback(IntPtr callback, IntPtr userData)
        {
            LastCallback = callback;
            LastUserData = userData;
        }
    }
}
