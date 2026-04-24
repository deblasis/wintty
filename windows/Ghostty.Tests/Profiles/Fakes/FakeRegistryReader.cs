using System.Collections.Generic;
using Ghostty.Core.Profiles;

namespace Ghostty.Tests.Profiles.Fakes;

/// <summary>
/// Dictionary-backed registry fake. Tests configure values via
/// <see cref="SetValue"/>; absent keys/values return null/false.
/// </summary>
internal sealed class FakeRegistryReader : IRegistryReader
{
    private readonly Dictionary<string, string> _values = new();
    private readonly HashSet<string> _keys = new();

    public void SetKey(RegistryHive hive, string keyPath)
        => _keys.Add(KeyKey(hive, keyPath));

    public void SetValue(RegistryHive hive, string keyPath, string valueName, string value)
    {
        _values[ValueKey(hive, keyPath, valueName)] = value;
        _keys.Add(KeyKey(hive, keyPath));
    }

    public string? ReadString(RegistryHive hive, string keyPath, string valueName)
        => _values.TryGetValue(ValueKey(hive, keyPath, valueName), out var v) ? v : null;

    public bool KeyExists(RegistryHive hive, string keyPath)
        => _keys.Contains(KeyKey(hive, keyPath));

    private static string KeyKey(RegistryHive hive, string keyPath) => $"{hive}::{keyPath}";

    private static string ValueKey(RegistryHive hive, string keyPath, string valueName)
        => $"{hive}::{keyPath}::{valueName}";
}
