using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghostty.Commands;

internal sealed class FrecencyStore
{
    public Dictionary<string, FrecencyEntry> Entries { get; set; } = [];

    public void RecordUse(string commandId)
    {
        if (Entries.TryGetValue(commandId, out var entry))
        {
            entry.UseCount++;
            entry.LastUsed = DateTime.UtcNow;
        }
        else
        {
            Entries[commandId] = new FrecencyEntry
            {
                UseCount = 1,
                LastUsed = DateTime.UtcNow,
            };
        }
    }

    public double Score(string commandId)
    {
        if (!Entries.TryGetValue(commandId, out var entry))
            return 0.0;

        var daysSince = (DateTime.UtcNow - entry.LastUsed).TotalDays;
        var recencyWeight = Math.Pow(0.9, daysSince);
        return entry.UseCount * recencyWeight;
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, FrecencyStoreContext.Default.FrecencyStore);
    }

    public static FrecencyStore FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new FrecencyStore();

        try
        {
            return JsonSerializer.Deserialize(json, FrecencyStoreContext.Default.FrecencyStore)
                ?? new FrecencyStore();
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"FrecencyStore parse failed: {ex.Message}");
            return new FrecencyStore();
        }
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Ghostty",
        "command-frecency.json");

    public static FrecencyStore Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new FrecencyStore();
            var json = File.ReadAllText(path);
            return FromJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FrecencyStore load failed: {ex.Message}");
            return new FrecencyStore();
        }
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ToJson());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FrecencyStore save failed: {ex.Message}");
        }
    }
}

internal sealed class FrecencyEntry
{
    public int UseCount { get; set; }
    public DateTime LastUsed { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FrecencyStore))]
internal partial class FrecencyStoreContext : JsonSerializerContext
{
}
