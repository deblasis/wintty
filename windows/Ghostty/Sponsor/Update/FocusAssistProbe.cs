using System;
using System.Diagnostics;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Queries whether the user wants to be notified right now.
/// Uses Win11 <see cref="Windows.UI.Shell.FocusSessionManager"/> when
/// available; falls back to Win32 SHQueryUserNotificationState on older
/// builds. Returns "allow notifications" if every query path fails.
/// </summary>
internal static class FocusAssistProbe
{
    /// <summary>True iff a notification would be appropriate.</summary>
    public static bool CanNotify()
    {
        try
        {
            if (Windows.UI.Shell.FocusSessionManager.IsSupported)
            {
                var mgr = Windows.UI.Shell.FocusSessionManager.GetDefault();
                if (mgr is not null && mgr.IsFocusActive)
                {
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] FocusSessionManager probe failed: {ex.Message}");
        }

        try
        {
            Windows.Win32.PInvoke.SHQueryUserNotificationState(out var state);
            return state == Windows.Win32.UI.Shell.QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] SHQueryUserNotificationState probe failed: {ex.Message}");
            return true;
        }
    }
}
