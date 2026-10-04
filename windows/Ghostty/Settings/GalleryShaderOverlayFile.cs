using System;
using System.IO;
using System.Security;
using Ghostty.Core;

namespace Ghostty.Settings;

/// <summary>
/// Writes the generated shader-token overlay body to an app-managed file so
/// <c>ConfigService</c> can layer it via <c>ghostty_config_load_file</c>. The
/// file lives in the per-user local app-data dir (same root as crash.log and
/// the High Contrast override), never the user's config, and holds only shader
/// paths -- no hostnames/usernames/secrets.
/// </summary>
/// <remarks>
/// The rendering is <see cref="Ghostty.Core.Settings.ShaderGalleryOverlay"/>'s;
/// this is the part that needs a filesystem. Best effort in the same way the
/// High Contrast override is: a config build that cannot write the overlay
/// layers nothing, and the surfaces keep whatever the config alone resolves
/// to.
/// </remarks>
internal static class GalleryShaderOverlayFile
{
    private const string FileName = "gallery-shaders.conf";

    /// <summary>
    /// Write <paramref name="body"/> and return its absolute path, or null if
    /// the file could not be written (caller then skips layering).
    /// </summary>
    public static string? Write(string body)
    {
        try
        {
            var path = OverlayPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
            return path;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Remove the overlay once no config is built from one. The next build
    /// rewrites it, and nothing reads it outside a build, so the state
    /// directory is not left holding the paths of the last gallery pick.
    /// Best effort; a locked or half-gone file costs a stale entry, nothing
    /// more.
    /// </summary>
    public static void Delete()
    {
        try
        {
            var path = OverlayPath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Swallowed on purpose; see the summary.
        }
    }

    private static string OverlayPath()
        => Path.Combine(
            AppStateBase.LocalRoot,
            AppIdentity.StateDirName,
            FileName);
}
