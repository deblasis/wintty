using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Windows.Win32;

namespace Ghostty.Hosting;

/// <summary>
/// Builds a Microsoft.UI.Input.InputCursor whose underlying HCURSOR is a
/// 32x32 fully-transparent bitmap. Used by the mouse-hide-while-typing
/// path: assigning this cursor to a UIElement's ProtectedCursor makes
/// the pointer visually disappear over that element, while libghostty's
/// (synthetic) showMouse re-assertions through the WinUI Lifted Input
/// stack remain harmless (they re-apply the same transparent HCURSOR).
///
/// Why not InputDesktopResourceCursor.Create or InputSystemCursor: the
/// resource-cursor path requires baking a .cur into the apphost's Win32
/// resources, which conflicts with the .NET SDK's ApplicationManifest +
/// ApplicationIcon auto-generation. The system-cursor enum has no
/// "invisible" value. CreateFromHCursor on IInputCursorStaticsInterop
/// is the documented public path that sidesteps both.
///
/// API contract: IInputCursorStaticsInterop COPIES the HCURSOR, so we
/// destroy the source HCURSOR immediately after wrapping. See
/// https://learn.microsoft.com/windows/windows-app-sdk/api/win32/microsoft.ui.input.inputcursor.interop/nf-microsoft-ui-input-inputcursor-interop-iinputcursorstaticsinterop-createfromhcursor
/// </summary>
internal static partial class InvisibleCursorFactory
{
    // IID for IInputCursorStaticsInterop (microsoft.ui.input.inputcursor.interop.h).
    private static readonly Guid IID_IInputCursorStaticsInterop =
        new("ac6f5065-90c4-46ce-beb7-05e138e54117");

    private const string InputCursorClassName = "Microsoft.UI.Input.InputCursor";

    [LibraryImport("combase.dll", EntryPoint = "WindowsCreateString", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, int length, out nint hstring);

    [LibraryImport("combase.dll", EntryPoint = "WindowsDeleteString")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll", EntryPoint = "RoGetActivationFactory")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    // Lazily-built, process-wide. Not thread-safe: today the only
    // caller is TerminalControl.SetMouseVisibility which runs on the
    // UI dispatcher thread, so concurrent first reads cannot race.
    // If that ever changes, wrap in Lazy<InputCursor> with
    // LazyThreadSafetyMode.ExecutionAndPublication.
    private static InputCursor? _invisible;

    /// <summary>
    /// Lazily-built, process-wide transparent cursor. Throws on first
    /// access if the COM interop or HCURSOR construction fails; callers
    /// should fall back to leaving the cursor visible if so.
    /// </summary>
    public static InputCursor Invisible
    {
        get
        {
            if (_invisible is not null) return _invisible;
            _invisible = Build();
            return _invisible;
        }
    }

    private static unsafe InputCursor Build()
    {
        // 32x32 1bpp cursor:
        //   AND mask = all 1s  -> use existing screen pixel (transparent)
        //   XOR mask = all 0s  -> no XOR contribution (irrelevant since AND=1)
        // Each scanline is 32 bits = 4 bytes; 32 scanlines = 128 bytes per mask.
        Span<byte> andMask = stackalloc byte[128];
        Span<byte> xorMask = stackalloc byte[128];
        andMask.Fill(0xFF);
        xorMask.Clear();

        nint hCursor;
        fixed (byte* pAnd = andMask)
        fixed (byte* pXor = xorMask)
        {
            hCursor = (nint)PInvoke.CreateCursor((Windows.Win32.Foundation.HINSTANCE)IntPtr.Zero, 0, 0, 32, 32, pAnd, pXor).Value;
        }
        if (hCursor == 0)
            throw new InvalidOperationException("Win32 CreateCursor returned NULL");

        try
        {
            int hr = WindowsCreateString(InputCursorClassName, InputCursorClassName.Length, out nint classId);
            if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"WindowsCreateString hr=0x{hr:X8}");

            try
            {
                hr = RoGetActivationFactory(classId, IID_IInputCursorStaticsInterop, out nint factoryAbi);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"RoGetActivationFactory hr=0x{hr:X8}");

                try
                {
                    // IInputCursorStaticsInterop vtable layout:
                    //   [0] IUnknown.QueryInterface
                    //   [1] IUnknown.AddRef
                    //   [2] IUnknown.Release
                    //   [3] IInspectable.GetIids
                    //   [4] IInspectable.GetRuntimeClassName
                    //   [5] IInspectable.GetTrustLevel
                    //   [6] IInputCursorStaticsInterop.CreateFromHCursor
                    nint* vtable = *(nint**)factoryAbi;
                    var createFromHCursor =
                        (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)vtable[6];

                    nint cursorAbi;
                    hr = createFromHCursor(factoryAbi, hCursor, &cursorAbi);
                    if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"CreateFromHCursor hr=0x{hr:X8}");

                    try
                    {
                        // CsWinRT projection wraps the IInputCursor ABI pointer into
                        // the strongly-typed Microsoft.UI.Input.InputCursor.
                        return WinRT.MarshalInspectable<InputCursor>.FromAbi(cursorAbi);
                    }
                    finally
                    {
                        Marshal.Release(cursorAbi);
                    }
                }
                finally
                {
                    Marshal.Release(factoryAbi);
                }
            }
            finally
            {
                WindowsDeleteString(classId);
            }
        }
        finally
        {
            // CreateFromHCursor copied the bits; safe to destroy our source.
            PInvoke.DestroyCursor(new Windows.Win32.UI.WindowsAndMessaging.HCURSOR(hCursor));
        }
    }
}
