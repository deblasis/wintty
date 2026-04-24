using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Profiles.Fakes;

namespace Ghostty.Tests.Profiles.Scenarios;

/// <summary>
/// One scenario, fully wired with fakes ready to be passed to a probe
/// or pure-logic type under test.
/// </summary>
internal sealed class Scenario
{
    public required FakeProcessRunner ProcessRunner { get; init; }
    public required FakeRegistryReader Registry { get; init; }
    public required FakeFileSystem FileSystem { get; init; }
}

/// <summary>
/// Loads a JSON scenario from Profiles\Scenarios\&lt;name&gt;.json
/// next to the test assembly.
/// </summary>
internal static class ScenarioLoader
{
    public static Scenario Load(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Profiles", "Scenarios");
        var path = Path.Combine(dir, name + ".json");
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize(json, ScenarioDtoContext.Default.ScenarioDto)
                  ?? throw new InvalidOperationException($"empty scenario: {name}");

        var fs = new FakeFileSystem();
        foreach (var (k, v) in dto.KnownFolders)
            fs.SetKnownFolder(Enum.Parse<KnownFolderId>(k), v);
        foreach (var f in dto.Files)
            fs.AddFile(f);

        var reg = new FakeRegistryReader();
        foreach (var k in dto.Registry.Keys)
        {
            // Format: "Hive::KeyPath".
            var parts = k.Split("::", 2);
            if (parts.Length != 2)
                throw new InvalidOperationException(
                    $"Registry key '{k}' must be in 'Hive::KeyPath' format.");
            reg.SetKey(Enum.Parse<RegistryHive>(parts[0]), parts[1]);
        }
        foreach (var v in dto.Registry.Values)
            reg.SetValue(
                Enum.Parse<RegistryHive>(v.Hive),
                v.KeyPath,
                v.ValueName,
                v.Value);

        var runner = new FakeProcessRunner();
        foreach (var p in dto.Processes)
            runner.EnqueueResult(
                p.FileName,
                p.Args,
                new ProcessResult(p.ExitCode, p.Stdout, p.Stderr, TimeSpan.Zero));

        return new Scenario
        {
            ProcessRunner = runner,
            Registry = reg,
            FileSystem = fs,
        };
    }
}

// DTOs match the JSON fixture shape exactly.
// Kept internal (not private nested) so [JsonSerializable] source-gen can reference them.

internal sealed class ScenarioDto
{
    public Dictionary<string, string> KnownFolders { get; set; } = new();
    public List<string> Files { get; set; } = new();
    public ScenarioRegistryDto Registry { get; set; } = new();
    public List<ScenarioProcessDto> Processes { get; set; } = new();
}

internal sealed class ScenarioRegistryDto
{
    public List<string> Keys { get; set; } = new();
    public List<ScenarioRegistryValueDto> Values { get; set; } = new();
}

internal sealed class ScenarioRegistryValueDto
{
    public string Hive { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string Value { get; set; } = "";
}

internal sealed class ScenarioProcessDto
{
    public string FileName { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public int ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
}

[JsonSerializable(typeof(ScenarioDto))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ScenarioDtoContext : JsonSerializerContext
{
}
