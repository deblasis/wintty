using System;

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
/// <para>
/// What this does not see: a drive letter mapped to a network share. <c>Z:\</c>
/// passes, and can still resolve to another machine. That is a weaker vector --
/// the mapping is one the user made, to a server they already authenticated to,
/// and a reported <c>Z:\</c> reaches nothing unless the mapping exists. The rule
/// here is about paths that NAME a host, not every path that can reach one.
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

        // Two leading separators of EITHER kind is UNC, because Win32
        // normalization folds '/' into '\' before it resolves the path. A
        // check that only knew the backslash spelling would let `//host/share`
        // through to CreateProcess and reach exactly the server it exists to
        // refuse.
        if (cwd.Length < 2 || !IsSeparator(cwd[0]) || !IsSeparator(cwd[1])) return true;

        var rest = cwd.AsSpan(2);

        // `\\?\` (extended-length) and `\\.\` (device) share a shape and a
        // meaning: what follows is not a server name. Both prefixes are
        // matched with literal backslashes because Windows does NOT normalize
        // separators inside them -- `//?/UNC/host/share` is not an extended
        // path at all, it is a plain UNC one naming the host `?`, which falls
        // through below and is refused. Strict here is what makes that true.
        if (rest.Length >= 2 && (rest[0] == '?' || rest[0] == '.')
            && cwd[0] == '\\' && cwd[1] == '\\' && rest[1] == '\\')
        {
            var tail = rest[2..];
            // `\\?\UNC\server\share` is a second spelling of the same reach,
            // so it is parsed rather than waved through.
            if (tail.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase))
                return HostIsLocal(tail[4..]);
            // `\\?\C:\dir` is the long-path spelling of a drive root.
            if (tail.Length >= 2 && char.IsAsciiLetter(tail[0]) && tail[1] == ':') return true;
            // The device namespace names no directory at all.
            return false;
        }

        return HostIsLocal(rest);
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary>
    /// Whether the server at the head of a UNC path (its <c>\\</c> or
    /// <c>\\?\UNC\</c> prefix already removed) is this machine.
    /// </summary>
    private static bool HostIsLocal(ReadOnlySpan<char> unc)
    {
        var end = unc.IndexOfAny('\\', '/');
        var host = end < 0 ? unc : unc[..end];
        if (host.IsEmpty) return false;

        foreach (var local in LocalShareHosts)
            if (host.Equals(local, StringComparison.OrdinalIgnoreCase)) return true;

        return host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}
