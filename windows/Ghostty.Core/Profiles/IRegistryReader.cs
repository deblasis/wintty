namespace Ghostty.Core.Profiles;

/// <summary>
/// Registry hive selector. Local enum so Ghostty.Core does not depend
/// on Microsoft.Win32.Registry, which is Windows-only at runtime even
/// though the types are cross-platform.
/// </summary>
public enum RegistryHive
{
    LocalMachine,
    CurrentUser,
}

/// <summary>
/// Read-only registry access. Probes never write. Production wrapper
/// uses Microsoft.Win32.Registry; tests use FakeRegistryReader.
/// </summary>
public interface IRegistryReader
{
    string? ReadString(RegistryHive hive, string keyPath, string valueName);
    bool KeyExists(RegistryHive hive, string keyPath);
}
