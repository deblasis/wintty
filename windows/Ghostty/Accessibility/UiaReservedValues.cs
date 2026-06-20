using System;
using System.Runtime.InteropServices;

namespace Ghostty.Accessibility;

/// <summary>
/// UIA reserved attribute-value sentinels for ITextRangeProvider.GetAttributeValue.
/// WinUI 3 does not surface these as managed constants, so we fetch the canonical
/// COM sentinels from UIAutomationCore once and reuse them. Each resolves to null
/// if it cannot be obtained; a null attribute value is itself treated as
/// "unsupported" by clients, so that is a safe degradation.
/// </summary>
internal static partial class UiaReservedValues
{
    // Resolved once on first use. Lazy gives thread-safe publication because UIA
    // GetAttributeValue can be called from UIA/RPC threads, not just the UI thread.
    private static readonly Lazy<object?> _notSupported =
        new(() => FromIUnknown(UiaGetReservedNotSupportedValue));
    private static readonly Lazy<object?> _mixed =
        new(() => FromIUnknown(UiaGetReservedMixedAttributeValue));

    /// <summary>The reserved "not supported" value, or null if unavailable.</summary>
    public static object? NotSupported() => _notSupported.Value;

    /// <summary>The reserved "mixed attribute value", or null if unavailable.</summary>
    public static object? Mixed() => _mixed.Value;

    private delegate int ReservedGetter(out IntPtr value);

    private static object? FromIUnknown(ReservedGetter getter)
    {
        try
        {
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

    // HRESULT UiaGetReservedMixedAttributeValue(IUnknown** value)
    [LibraryImport("UIAutomationCore.dll")]
    private static partial int UiaGetReservedMixedAttributeValue(out IntPtr value);
}
