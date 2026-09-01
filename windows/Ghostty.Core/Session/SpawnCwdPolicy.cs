namespace Ghostty.Core.Session;

/// <summary>
/// Whether a directory a SHELL reported may be spawned into.
/// </summary>
/// <remarks>
/// <para>
/// A reported cwd is not a user preference; it is bytes that arrived on the
/// pty, and anything writing there can emit one -- <c>cat</c> of a hostile
/// file, or a remote session the user is logged into. It then becomes a spawn
/// directory: Duplicate Tab, Reopen Closed Tab, Reopen Closed Window and
/// session restore all hand it to CreateProcess. Windows opens an SMB
/// connection to whatever server a UNC directory names, and authenticates to
/// it, so a reported <c>\\host\share</c> is a way to choose who receives the
/// user's credentials.
/// </para>
/// <para>
/// The terminal core refuses such a report at the source, but this layer is
/// the one that reaches CreateProcess, and it also reads cwds persisted by
/// earlier builds that had no such check. A share the user genuinely works on
/// costs them cwd inheritance -- the tab opens at the profile's directory
/// instead -- which is the direction worth being wrong in.
/// </para>
/// <para>
/// A directory the USER configured is not subject to this: a profile's
/// working-directory is theirs to set. Only the reported value passes here.
/// </para>
/// </remarks>
internal static class SpawnCwdPolicy
{
    /// <summary>
    /// Share hosts that resolve without leaving this machine, and so can
    /// never carry a credential off it. <c>wsl.localhost</c> and its legacy
    /// spelling <c>wsl$</c> are served by the local WSL service rather than
    /// by SMB over the wire; the loopback names reach this machine's own SMB
    /// server. Windows host names are case-insensitive, and so is this.
    /// </summary>
    private static readonly string[] LocalShareHosts =
        ["wsl.localhost", "wsl$", "localhost", "127.0.0.1", "::1"];

    public static bool MaySpawnAt(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return false;
        if (!cwd.StartsWith(@"\\", System.StringComparison.Ordinal)) return true;

        var rest = cwd.AsSpan(2);

        // `\\?\` (extended-length) and `\\.\` (device) share a shape and a
        // meaning: what follows is not a server name. `\\?\UNC\server\share`
        // is a second spelling of the same reach, so it is parsed rather
        // than waved through.
        if (rest.Length >= 2 && (rest[0] == '?' || rest[0] == '.') && rest[1] == '\\')
        {
            var tail = rest[2..];
            if (tail.StartsWith("UNC\\", System.StringComparison.OrdinalIgnoreCase))
                return HostIsLocal(tail[4..]);
            // `\\?\C:\dir` is the long-path spelling of a drive root.
            if (tail.Length >= 2 && char.IsAsciiLetter(tail[0]) && tail[1] == ':') return true;
            // The device namespace names no directory at all.
            return false;
        }

        return HostIsLocal(rest);
    }

    /// <summary>
    /// Whether the server at the head of a UNC path (its <c>\\</c> or
    /// <c>\\?\UNC\</c> prefix already removed) is this machine.
    /// </summary>
    private static bool HostIsLocal(System.ReadOnlySpan<char> unc)
    {
        var end = unc.IndexOfAny('\\', '/');
        var host = end < 0 ? unc : unc[..end];
        if (host.IsEmpty) return false;

        foreach (var local in LocalShareHosts)
            if (host.Equals(local, System.StringComparison.OrdinalIgnoreCase)) return true;

        return host.Equals(System.Environment.MachineName, System.StringComparison.OrdinalIgnoreCase);
    }
}
