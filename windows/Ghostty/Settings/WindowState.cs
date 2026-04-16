using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghostty.Settings;

/// <summary>
/// Persistent window state (placement only). The UI preferences
/// that used to live alongside these fields (vertical-tabs, command
/// palette backdrop + grouping) have moved to the real ghostty
/// config file via <see cref="WindowStateMigration"/>. What remains
/// is strictly ephemeral shell state -- the window's last geometry
/// -- that must not pollute the user's committed ghostty config.
/// One JSON file at <c>%APPDATA%\Ghostty\window-state.json</c>,
/// loaded once at startup and written on window close.
///
/// Kept separate from the real config for two reasons: (1) this
/// value churns on every close, which would make the ghostty config
/// file noisy under source control, and (2) libghostty does not know
/// about window placement -- it is purely a shell concern.
///
/// Serialization goes through <see cref="WindowStateContext"/> (a
/// source-generated <see cref="JsonSerializerContext"/>) so this
/// stays trim/AOT-safe if/when <c>PublishAot</c> is turned on.
/// Reflection-based STJ would root type metadata and silently drop
/// properties under the trimmer (PowerToys # 42644).
/// </summary>
internal sealed class WindowState
{
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    private static string Dir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Ghostty");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    internal static string FilePath => Path.Combine(Dir, "window-state.json");

    // Pre-migration path. On first launch after the UiSettings split,
    // Load falls back to this file so window placement is preserved
    // across the rename. Once the new file is written on close, this
    // path is ignored on subsequent launches.
    internal static string LegacyFilePath => Path.Combine(Dir, "ui-settings.json");

    public static WindowState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath),
                    WindowStateContext.Default.WindowState) ?? new WindowState();

            if (File.Exists(LegacyFilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(LegacyFilePath),
                    WindowStateContext.Default.WindowState) ?? new WindowState();

            return new WindowState();
        }
        catch (Exception ex)
        {
            // A malformed or inaccessible state file must never block
            // startup. Fall back to defaults and trace for post-mortem.
            Debug.WriteLine($"WindowState load failed, using defaults: {ex.Message}");
            return new WindowState();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, WindowStateContext.Default.WindowState));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WindowState save failed: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowState))]
internal partial class WindowStateContext : JsonSerializerContext
{
}
