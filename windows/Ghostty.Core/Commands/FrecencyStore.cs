using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghostty.Commands;

internal sealed partial class FrecencyStore
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
            CoreStaticLoggers.FrecencyStore.LogParseFailed(ex);
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
            CoreStaticLoggers.FrecencyStore.LogLoadFailed(ex);
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
            CoreStaticLoggers.FrecencyStore.LogSaveFailed(ex);
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

internal static partial class FrecencyStoreLogExtensions
{
    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Frecency.ParseFailed,
                   Level = LogLevel.Warning,
                   Message = "FrecencyStore parse failed")]
    internal static partial void LogParseFailed(
        this ILogger<FrecencyStore> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Frecency.LoadFailed,
                   Level = LogLevel.Warning,
                   Message = "FrecencyStore load failed")]
    internal static partial void LogLoadFailed(
        this ILogger<FrecencyStore> logger, System.Exception ex);

    [LoggerMessage(EventId = Ghostty.Core.Logging.LogEvents.Frecency.SaveFailed,
                   Level = LogLevel.Warning,
                   Message = "FrecencyStore save failed")]
    internal static partial void LogSaveFailed(
        this ILogger<FrecencyStore> logger, System.Exception ex);
}
