namespace Ghostty.Core.Version;

/// <summary>
/// Immutable snapshot of all fields surfaced by <c>wintty +version</c>
/// and the Version command-palette dialog. Built once via
/// <see cref="VersionRenderer.Build"/> and rendered to either plain text
/// or ANSI by the renderer.
/// </summary>
public sealed record VersionInfo(
    // Wintty-side (BuildInfo.g.cs constants)
    string WinttyVersion,
    // Optional free-form build identifier, empty by default. Rendered only
    // when non-empty so parallel local builds are tellable apart.
    string BuildLabel,
    string WinttyVersionString,
    string WinttyCommit,
    Edition Edition,
    // libghostty (FFI)
    LibGhosttyBuildInfo LibGhostty,
    // Compile-time C#
    string DotnetRuntime,
    string MsbuildConfig,
    // Constants the host always knows
    string AppRuntime,
    string Renderer,
    string FontEngine,
    // Runtime probes
    string WindowsVersion,
    string Architecture);
