using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghostty.Settings;

/// <summary>
/// Tiny persistent store for UI-layer preferences that need to
/// survive restarts but do not belong in libghostty's config layer
/// yet. One JSON file at
/// <c>%APPDATA%\Ghostty\ui-settings.json</c>, loaded once at
/// startup and written on every change.
///
/// Serialization goes through <see cref="UiSettingsContext"/> (a
/// source-generated <see cref="JsonSerializerContext"/>) so this
/// stays trim/AOT-safe if/when <c>PublishAot</c> is turned on.
/// Reflection-based STJ would root type metadata and silently
/// drop properties under the trimmer (PowerToys #42644).
///
/// TODO(config): fold this into the real config layer when it lands
/// on Windows. Until then this is the only piece of per-user UI
/// state (vertical-vs-horizontal tabs) that has to persist.
/// </summary>
internal sealed class UiSettings
{
    public bool VerticalTabs { get; set; }

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Ghostty");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ui-settings.json");
        }
    }

    public static UiSettings Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new UiSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, UiSettingsContext.Default.UiSettings)
                ?? new UiSettings();
        }
        catch (Exception ex)
        {
            // A malformed or inaccessible settings file must never
            // block startup. Fall back to defaults and trace the
            // reason for post-mortem.
            Debug.WriteLine($"UiSettings load failed, using defaults: {ex.Message}");
            return new UiSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, UiSettingsContext.Default.UiSettings);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            // Same policy as Load: never throw from a settings write.
            Debug.WriteLine($"UiSettings save failed: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiSettings))]
internal partial class UiSettingsContext : JsonSerializerContext
{
}
