namespace Ghostty.Core.JumpList;

/// <summary>
/// Stub data type for a jump-list profile entry. The shape drives
/// what the config layer will need to produce once it lands; for
/// this PR every instance comes from an empty list and none of
/// these fields are read.
///
/// <see cref="Id"/> is the stable identifier the jump list invokes
/// back (e.g. "pwsh", "cmd", "wsl-ubuntu"). <see cref="DisplayName"/>
/// is the human text shown in the menu. <see cref="IconPath"/> is a
/// .exe or .ico file the Shell will rasterise; null means the
/// default Ghostty icon. <see cref="ShellCommand"/> and
/// <see cref="WorkingDirectory"/> are forwarded to the spawned
/// process when the entry is clicked.
///
/// TODO(config): replace the Array.Empty pathway in
/// <see cref="JumpListBuilder"/> with real profiles once the
/// config layer exists.
/// </summary>
internal sealed record ProfileEntry(
    string Id,
    string DisplayName,
    string? IconPath,
    string? ShellCommand,
    string? WorkingDirectory);
