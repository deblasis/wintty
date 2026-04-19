using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Logging;

/// <summary>
/// Forwards libghostty's Zig std.log output into the process-wide
/// <see cref="ILoggerFactory"/> so Zig messages land in every sink the
/// C# shell already uses (ETW, rolling file, anything else the bootstrap
/// registers).
///
/// libghostty calls <c>ghostty_log_set_callback(cb, user_data)</c> to
/// register an embedder callback. Without that bridge the Windows
/// GUI-subsystem exe gets nothing: stderr is disconnected and macOS
/// unified logging is not an option. The callback is invoked from
/// whichever thread emits a log call, so the bridge makes no assumption
/// about which thread runs <see cref="Log"/>.
///
/// Level mapping (contract pinned on the Zig side in log_bridge.zig):
///   0 -> <see cref="LogLevel.Debug"/>
///   1 -> <see cref="LogLevel.Information"/>
///   2 -> <see cref="LogLevel.Warning"/>
///   3 -> <see cref="LogLevel.Error"/>
///
/// Category format: <c>Ghostty.Zig.&lt;scope&gt;</c>. Scope comes
/// straight from Zig's <c>@tagName(scope)</c>. For the default scope
/// (empty bytes) we use <c>Ghostty.Zig</c> with no suffix.
/// </summary>
internal sealed class LibghosttyLogBridge : IDisposable
{
    /// <summary>
    /// Abstracts the native <c>ghostty_log_set_callback</c> call so the
    /// bridge can be exercised in tests without loading libghostty.dll.
    /// The real implementation (in the WinUI 3 shell project) P/Invokes
    /// through <c>NativeMethods.LogSetCallback</c>.
    /// </summary>
    internal interface INativeInstaller
    {
        void SetCallback(IntPtr callback, IntPtr userData);
    }

    /// <summary>
    /// Matches <c>ghostty_log_set_callback</c>'s callback signature in
    /// include/ghostty.h (exported by src/log_bridge.zig). Bytes are
    /// NOT null-terminated; use the companion length. The corresponding
    /// delegate in the WinUI shell's NativeMethods uses the same shape;
    /// both must stay in lockstep.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallbackDelegate(
        uint level,
        IntPtr scopePtr,
        UIntPtr scopeLen,
        IntPtr messagePtr,
        UIntPtr messageLen,
        IntPtr userData);

    // Logger category prefix. Shared as a constant so the smoke parser
    // on the parent test harness can match emission by prefix.
    internal const string CategoryPrefix = "Ghostty.Zig";
    internal const string DefaultCategory = "Ghostty.Zig";

    private readonly ILoggerFactory _factory;
    private readonly INativeInstaller _installer;

    // Keep the callback delegate alive through a strong field. Zig
    // holds a raw function pointer via Marshal.GetFunctionPointerForDelegate;
    // if the delegate were collected the next callback would jump into
    // freed memory. A plain managed reference plus the bridge's own
    // lifetime is enough -- the delegate is unreferenced from native
    // code as soon as Dispose clears the callback.
    private readonly LogCallbackDelegate _callback;
    private readonly IntPtr _callbackPtr;

    // One ILogger per unique scope. Zig scope names are small and
    // bounded (enum tag names from src/log.zig), so the cache size
    // stays tiny. ConcurrentDictionary because the callback may fire
    // concurrently from multiple Zig threads. Post-warmup this cache
    // amortizes the per-call UTF-8 decode and string concat in
    // ReadCategory; a cold call allocates two strings, a warm call
    // allocates one (the scope) and reuses the cached logger.
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);

    // Static factory delegate for ConcurrentDictionary.GetOrAdd's
    // (TKey, Func<TKey, TArg, TValue>, TArg) overload. Passing a
    // static method group + state avoids allocating a fresh closure
    // delegate on every OnLog call.
    private static readonly Func<string, ILoggerFactory, ILogger> s_createLogger =
        static (category, factory) => factory.CreateLogger(category);

    private int _disposed; // 0=live, 1=disposed; swapped via Interlocked.

    internal LibghosttyLogBridge(ILoggerFactory factory, INativeInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(installer);
        _factory = factory;
        _installer = installer;

        // Build the delegate once. GetFunctionPointerForDelegate gives
        // a pointer that Zig stores verbatim; we hand the same pointer
        // back to clear the callback on Dispose, via null.
        _callback = OnLog;
        _callbackPtr = Marshal.GetFunctionPointerForDelegate(_callback);
    }

    /// <summary>
    /// Register the callback with libghostty. After this returns, every
    /// Zig log call produces an <see cref="ILogger"/> emission under
    /// category <c>Ghostty.Zig.&lt;scope&gt;</c>.
    /// </summary>
    internal void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _installer.SetCallback(_callbackPtr, IntPtr.Zero);
    }

    /// <summary>
    /// Clear the callback and drop the delegate reference. Safe to call
    /// multiple times. After disposal any in-flight Zig thread that
    /// already latched the callback pointer still runs to completion
    /// (the GCHandle keeps the delegate alive via the field until this
    /// object itself is collected), but libghostty will not issue any
    /// new invocations.
    /// </summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Best-effort: clear the native side first so new calls stop.
        try { _installer.SetCallback(IntPtr.Zero, IntPtr.Zero); }
        catch { /* logging must not throw on teardown */ }
    }

    // Invoked by libghostty from any thread. MUST NOT throw -- an
    // exception leaking into native code is undefined behavior. Every
    // step is wrapped in a try/catch that swallows the failure; a
    // single malformed log line should never crash the process.
    private void OnLog(
        uint level,
        IntPtr scopePtr,
        UIntPtr scopeLen,
        IntPtr messagePtr,
        UIntPtr messageLen,
        IntPtr userData)
    {
        if (_disposed != 0) return;

        try
        {
            var logLevel = MapLevel(level);
            var category = ReadCategory(scopePtr, (int)scopeLen);
            var message = ReadUtf8(messagePtr, (int)messageLen);

            var logger = _loggers.GetOrAdd(category, s_createLogger, _factory);
            if (!logger.IsEnabled(logLevel)) return;

            // Pass the already-formatted Zig message as state so MEL
            // does not try to parse {placeholders} inside it. Format
            // argument "{Message}" keeps EventSource / file-sink output
            // identical to what Zig already rendered.
            logger.Log(
                logLevel: logLevel,
                eventId: default,
                state: message,
                exception: null,
                formatter: static (s, _) => s);
        }
        catch (Exception ex)
        {
            // Never let an exception leak into native code (that would
            // be UB across the C ABI). Best-effort surface the failure
            // to a dedicated category so this path is not silent if it
            // ever fires; the inner try/catch is there because the
            // failure may itself be a disposed-factory race during
            // shutdown.
            try
            {
                _factory.CreateLogger(BridgeFailureCategory)
                    .LogError(ex, "Log bridge callback failed");
            }
            catch
            {
                // If the diagnostic log also fails, truly swallow.
            }
        }
    }

    // Dedicated category for bridge-internal failures (marshal errors,
    // factory races). Kept separate from the Ghostty.Zig.* namespace so
    // users can filter it independently.
    private const string BridgeFailureCategory = "Ghostty.Zig.Bridge";

    /// <summary>
    /// Map the Zig-side ordinal to a Microsoft.Extensions.Logging level.
    /// Unknown integers are surfaced as <see cref="LogLevel.Information"/>
    /// so a future Zig-side level addition degrades to a safe default
    /// instead of being dropped.
    /// </summary>
    internal static LogLevel MapLevel(uint level) => level switch
    {
        0 => LogLevel.Debug,
        1 => LogLevel.Information,
        2 => LogLevel.Warning,
        3 => LogLevel.Error,
        _ => LogLevel.Information,
    };

    /// <summary>
    /// Decode a non-null-terminated UTF-8 byte range into a managed
    /// string. Returns the empty string for a null pointer or zero
    /// length. Exposed for tests.
    /// </summary>
    internal static string ReadUtf8(IntPtr ptr, int len)
    {
        if (ptr == IntPtr.Zero || len <= 0) return string.Empty;
        // PtrToStringUTF8(IntPtr, int) takes a non-null-terminated
        // UTF-8 buffer + byte length and returns a managed string,
        // without requiring an unsafe block. Available on .NET 6+.
        return Marshal.PtrToStringUTF8(ptr, len);
    }

    /// <summary>
    /// Produce the ILogger category name for a given Zig scope. Empty
    /// scope maps to <see cref="DefaultCategory"/>, anything else maps
    /// to <c>Ghostty.Zig.&lt;scope&gt;</c>. Exposed for tests.
    /// </summary>
    internal static string ReadCategory(IntPtr scopePtr, int scopeLen)
    {
        if (scopePtr == IntPtr.Zero || scopeLen <= 0) return DefaultCategory;
        var scope = ReadUtf8(scopePtr, scopeLen);
        return string.IsNullOrEmpty(scope) ? DefaultCategory : CategoryPrefix + "." + scope;
    }
}
