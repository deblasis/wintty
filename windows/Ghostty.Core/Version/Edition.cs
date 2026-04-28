namespace Ghostty.Core.Version;

/// <summary>
/// Build edition surfaced in the version output. The public repo only
/// emits <see cref="Oss"/>. The wintty-release overlay extends this enum
/// with sponsor and pro values.
/// </summary>
public enum Edition
{
    Oss,
}
