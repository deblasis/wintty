using System;
using System.IO;

namespace Ghostty.Accessibility;

/// <summary>
/// Writes the High Contrast override config body to an app-managed file so
/// ConfigService can layer it via ghostty_config_load_file. The file lives
/// in the per-user local app-data dir (same root as crash.log), never the
/// user's config, and holds only colors -- no hostnames/usernames/secrets.
/// </summary>
internal static class HighContrastOverrideFile
{
    /// <summary>
    /// Write <paramref name="body"/> and return its absolute path, or null
    /// if the file could not be written (caller then skips layering and the
    /// surface keeps the user's colors -- a safe degradation).
    /// </summary>
    public static string? Write(string body)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wintty");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "high-contrast.conf");
            File.WriteAllText(path, body);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
