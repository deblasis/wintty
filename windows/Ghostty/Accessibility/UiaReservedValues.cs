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
    private static object? _notSupported;
    private static bool _notSupportedResolved;
    private static object? _mixed;
    private static bool _mixedResolved;

    /// <summary>The reserved "not supported" value, or null if unavailable.</summary>
    public static object? NotSupported()
    {
        if (_notSupportedResolved) return _notSupported;
        _notSupportedResolved = true;
        _notSupported = FromIUnknown(UiaGetReservedNotSupportedValue);
        return _notSupported;
    }

    /// <summary>The reserved "mixed attribute value", or null if unavailable.</summary>
    public static object? Mixed()
    {
        if (_mixedResolved) return _mixed;
        _mixedResolved = true;
        _mixed = FromIUnknown(UiaGetReservedMixedAttributeValue);
        return _mixed;
    }

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
