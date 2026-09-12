using System;

namespace Ghostty.Core;

/// <summary>
/// The root every <see cref="AppIdentity"/>-derived state path is built
/// on: the per-user known folder, or the <c>WINTTY_STATE_BASE</c>
/// override when it is set.
///
/// The override exists for processes, not users: Wintty is multi-process
/// and every process of an edition shares one set of state files, so a
/// harness that needs two instances with separate state (#822's
/// GUI-level multi-instance tests) or a launch that must not touch the
/// real per-user tree has no other lever -- the paths were resolved
/// straight from the known-folder API at each call site, compile-time in
/// effect, with nothing in the environment able to move them. Setting
/// <c>WINTTY_STATE_BASE</c> to a directory moves every one of those
/// paths under it at once, both the <c>LocalApplicationData</c> half
/// (logs, crash.log, gpu.log, icon cache, discovery cache) and the
/// <c>ApplicationData</c> half (session.json, window-state.json,
/// command frecency), keeping the <see cref="AppIdentity.StateDirName"/>
/// layout segment so an overridden tree reads exactly like a real one.
///
/// What it deliberately does NOT move: the config tree (that is
/// <c>XDG_CONFIG_HOME</c>'s job, guarded by
/// <c>Ghostty.Core.Config.TestConfigGuard</c>), and libghostty's own
/// caches, which the native side resolves without this class. A launch
/// that needs both moved sets sets both variables.
///
/// A user's install never sets the variable and must never notice this
/// class exists: unset, null, empty or whitespace-only all mean "use the
/// real known folder", which is byte-for-byte the behaviour the call
/// sites had before the override.
/// </summary>
internal static class AppStateBase
{
    /// <summary>
    /// The env var that overrides the state root. Its value is the
    /// directory itself (not a flag spelling), read on every resolution:
    /// the decision belongs to whoever launched this process, and a test
    /// flipping the variable between calls is the cheapest red/green
    /// pair there is.
    /// </summary>
    public const string EnvVar = "WINTTY_STATE_BASE";

    /// <summary>
    /// The one place the environment is read, so tests can inject a
    /// dictionary instead of mutating the real process environment.
    /// Mutating the real environment from a test races every
    /// concurrently running collection and transiently redirects every
    /// other test's state paths; an injected reader touches nothing
    /// outside the test itself. INTERNAL on purpose, matching
    /// <c>TestConfigGuard.ReadEnvironment</c>: the seam exists for the
    /// test assemblies and nothing else.
    /// </summary>
    internal static Func<string, string?> ReadEnvironment { get; set; } =
        Environment.GetEnvironmentVariable;

    /// <summary>
    /// The override value, or null when it is unset or blank. The one
    /// normalization applied is a trim: a stray space or trailing quote
    /// from a hand-written <c>set</c> must not silently become part of
    /// the path, and a whitespace-only value is treated as unset rather
    /// than combined into a relative path.
    /// </summary>
    public static string? OverrideRoot =>
        NormalizeOverride(ReadEnvironment(EnvVar));

    /// <summary>
    /// The pure half of <see cref="OverrideRoot"/>, separated so the
    /// blank/trim handling is testable without touching any environment.
    /// </summary>
    internal static string? NormalizeOverride(string? rawValue) =>
        string.IsNullOrWhiteSpace(rawValue) ? null : rawValue.Trim();

    /// <summary>
    /// <paramref name="realRoot"/> with the override applied: the
    /// override when set, the argument untouched when not. For call
    /// sites that resolve their root through an injected abstraction of
    /// their own (the icon resolver's <c>IFileSystem.GetKnownFolder</c>)
    /// and therefore cannot use the properties below.
    /// </summary>
    public static string ApplyOverride(string realRoot) =>
        OverrideRoot ?? realRoot;

    /// <summary>
    /// The root of the local (non-roaming) state tree:
    /// <c>%LOCALAPPDATA%</c>, or the override. May be empty exactly when
    /// <see cref="Environment.GetFolderPath"/> returns empty (no
    /// profile), which call sites reject rather than combine into a
    /// relative path.
    /// </summary>
    public static string LocalRoot =>
        ApplyOverride(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// The root of the roaming state tree: <c>%APPDATA%</c>, or the
    /// override. With the override set, this and <see cref="LocalRoot"/>
    /// deliberately resolve to the same directory: the override trades
    /// the local/roaming split, which no harness needs, for one tree to
    /// point at and delete.
    /// </summary>
    public static string RoamingRoot =>
        ApplyOverride(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData));
}
