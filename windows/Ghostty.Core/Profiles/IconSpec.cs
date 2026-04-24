namespace Ghostty.Core.Profiles;

/// <summary>
/// Polymorphic icon source. Resolution from <see cref="IconSpec"/> to
/// rendered bytes is the responsibility of IIconResolver;
/// this type is just the spec.
/// </summary>
public abstract record IconSpec
{
    /// <summary>
    /// Absolute filesystem path to an .ico, .png, .jpg, or .svg file.
    /// </summary>
    public sealed record Path(string FilePath) : IconSpec;

    /// <summary>
    /// Segoe MDL2 / Fluent icon font code point.
    /// </summary>
    public sealed record Mdl2Token(int CodePoint) : IconSpec;

    /// <summary>
    /// Wintty-bundled icon by string key (e.g. "pwsh", "cmd", "wsl").
    /// </summary>
    public sealed record BundledKey(string Key) : IconSpec;

    /// <summary>
    /// Extract the icon associated with a Windows .exe via SHGetFileInfo.
    /// </summary>
    public sealed record AutoForExe(string ExePath) : IconSpec;

    /// <summary>
    /// Extract the per-distro WSL icon (wslg-managed); fall back to
    /// bundled "wsl" if the per-distro icon is unavailable.
    /// </summary>
    public sealed record AutoForWslDistro(string DistroName) : IconSpec;

    private IconSpec() { }
}
