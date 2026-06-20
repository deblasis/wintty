using System;
using System.Runtime.InteropServices;

namespace Ghostty.Accessibility;

/// <summary>
/// The UIA reserved "not supported" attribute value for
/// ITextRangeProvider.GetAttributeValue. WinUI 3 does not surface it as a managed
/// constant, so we fetch the canonical COM sentinel from UIAutomationCore once and
/// reuse it; it resolves to null if it cannot be obtained, and a null attribute
/// value is itself treated as "unsupported" by clients, so that is a safe
/// degradation.
/// </summary>
/// <remarks>
/// There is also a reserved "mixed attribute value" sentinel, but WinUI 3's
/// ITextRangeProvider projection does not pass it through to clients (verified
/// live: a multi-color range surfaces as NotSupported, not Mixed), so we do not
/// fetch it. The provider reports NotSupported for mixed ranges instead.
/// </remarks>
internal static partial class UiaReservedValues
{
    // Resolved once on first use. Lazy gives thread-safe publication because UIA
    // GetAttributeValue can be called from UIA/RPC threads, not just the UI thread.
    private static readonly Lazy<object?> _notSupported =
        new(() => FromIUnknown(UiaGetReservedNotSupportedValue));

    /// <summary>The reserved "not supported" value, or null if unavailable.</summary>
    public static object? NotSupported() => _notSupported.Value;

    private delegate int ReservedGetter(out IntPtr value);

    private static object? FromIUnknown(ReservedGetter getter)
    {
        try
        {
            // UIA reserved values are process-lifetime singletons, so we do not
            // Release p: the RCW's own reference is harmless against an object
            // that outlives the process, and releasing a borrowed static pointer
            // would risk an over-release.
            if (getter(out var p) == 0 && p != IntPtr.Zero)
                return Marshal.GetObjectForIUnknown(p);
        }
        catch
        {
            // Defensive: a missing export or marshalling failure degrades to null.
        }
        return null;
    }

    // HRESULT UiaGetReservedNotSupportedValue(IUnknown** value)
    [LibraryImport("UIAutomationCore.dll")]
    private static partial int UiaGetReservedNotSupportedValue(out IntPtr value);
}
