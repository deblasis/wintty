namespace Ghostty.Core.JumpList;

/// <summary>
/// One pinned-profile row on the Windows jump list.
///
/// <see cref="Id"/> is the stable identifier the jump list invokes
/// back via <c>--jumplist-profile=</c>. <see cref="DisplayName"/>
/// is the human text shown in the menu. <see cref="IconPath"/> is a
/// .exe or .ico file the Shell will rasterise; null means the
/// default app icon. <see cref="ShellCommand"/> and
/// <see cref="WorkingDirectory"/> describe the profile; the running
/// instance resolves the id through <c>IProfileRegistry</c> rather
/// than re-parsing these fields from argv.
/// </summary>
internal sealed record ProfileEntry(
    string Id,
    string DisplayName,
    string? IconPath,
    string? ShellCommand,
    string? WorkingDirectory);
