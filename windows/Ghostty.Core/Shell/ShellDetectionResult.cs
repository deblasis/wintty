namespace Ghostty.Core.Shell;

/// <summary>
/// Output of <see cref="ShellDetector.Detect(string)"/>. Structured so
/// callers do not have to switch on the enum just to decide whether to
/// log an unrecognized binary: <see cref="IsKnown"/> is the signal.
/// </summary>
/// <param name="Capability">VT/Console-API verdict.</param>
/// <param name="NormalizedFileName">
/// Lowercase leaf filename (e.g. <c>"pwsh.exe"</c>). Empty string when
/// the input has no file component. Nullable because <c>default</c>
/// struct initialization leaves it null; <see cref="ShellDetector.Detect"/>
/// always returns a non-null value.
/// </param>
public readonly record struct ShellDetectionResult(
    ShellCapability Capability,
    string? NormalizedFileName)
{
    /// <summary>
    /// True when the filename matched the known-shell table.
    /// </summary>
    public bool IsKnown => Capability != ShellCapability.Unknown;
}
