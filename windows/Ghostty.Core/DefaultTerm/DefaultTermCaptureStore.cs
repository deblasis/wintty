using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ghostty.Core.DefaultTerm;

/// <summary>One shared-group value captured before an enable rewrote it.</summary>
public sealed record CapturedSharedValue(
    string Subkey,
    string? Name,
    string Value);

/// <summary>
/// The registry state an enable found before it took over: the prior
/// selection pair, the prior console binary, and the prior shared-group
/// values. A corrupt or missing store reads as the nil state; a nil
/// state never restores anything.
/// </summary>
public sealed record CapturedState(
    bool HasSelectionPrior,
    string? PriorConsoleValue,
    string? PriorTerminalValue,
    string? PriorConsoleBinary,
    IReadOnlyList<CapturedSharedValue> SharedGroupValues,
    DateTimeOffset CapturedAtUtc)
{
    public static CapturedState Nil { get; } =
        new(false, null, null, null, Array.Empty<CapturedSharedValue>(), DateTimeOffset.MinValue);
}

/// <summary>
/// Persists the capture state as one JSON file. Reads never throw: a
/// missing, unreadable, or corrupt file is the nil state, so a torn store
/// degrades to "remove on disable" instead of blocking the user's choice.
/// </summary>
public sealed class DefaultTermCaptureStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DefaultTermCaptureStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public CapturedState Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return CapturedState.Nil;
            }

            var state = JsonSerializer.Deserialize<CapturedStateDto>(File.ReadAllText(Path), Options);
            if (state is null)
            {
                return CapturedState.Nil;
            }

            var shared = state.Shared?
                .Select(s => new CapturedSharedValue(s.Subkey ?? string.Empty, s.Name, s.Value ?? string.Empty))
                .ToArray()
                ?? Array.Empty<CapturedSharedValue>();
            var hasSelectionPrior =
                state.PriorConsoleValue is not null || state.PriorTerminalValue is not null;
            return new CapturedState(
                hasSelectionPrior,
                state.PriorConsoleValue,
                state.PriorTerminalValue,
                state.PriorConsoleBinary,
                shared,
                state.CapturedAtUtc ?? DateTimeOffset.MinValue);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return CapturedState.Nil;
        }
    }

    public void Save(CapturedState state)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var dto = new CapturedStateDto
        {
            PriorConsoleValue = state.PriorConsoleValue,
            PriorTerminalValue = state.PriorTerminalValue,
            PriorConsoleBinary = state.PriorConsoleBinary,
            CapturedAtUtc = state.CapturedAtUtc,
            Shared = state.SharedGroupValues
                .Select(v => new CapturedSharedValueDto { Subkey = v.Subkey, Name = v.Name, Value = v.Value })
                .ToArray(),
        };
        File.WriteAllText(Path, JsonSerializer.Serialize(dto, Options));
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A store that cannot be deleted does not block the disable;
            // a stale file only means a nil prior next time it is read.
        }
    }

    private sealed class CapturedStateDto
    {
        public string? PriorConsoleValue { get; set; }

        public string? PriorTerminalValue { get; set; }

        public string? PriorConsoleBinary { get; set; }

        public DateTimeOffset? CapturedAtUtc { get; set; }

        public CapturedSharedValueDto[]? Shared { get; set; }
    }

    private sealed class CapturedSharedValueDto
    {
        public string? Subkey { get; set; }

        public string? Name { get; set; }

        public string? Value { get; set; }
    }
}
