using System;
using System.Runtime.InteropServices;

namespace Ghostty.Core.Version;

/// <summary>
/// The one-line build identity for the artifacts that outlive the process:
/// the rolling log's startup record, gpu.log's header and the crash log's
/// entry all carry the same <see cref="VersionHeader.Compose"/> line, so a
/// file pasted alone answers "what was running" without a separate
/// +version (#968).
///
/// Computed once and cached, deliberately: RedirectStderrToFile runs before
/// InitGhostty on every path (gpu.log opens before any native code), and
/// ReportFatal must not be calling into libghostty at crash time when the
/// crash may be inside it. The FFI read is wrapped: a ghostty.dll that
/// cannot load still leaves a header built from the managed BuildInfo
/// constants alone - VersionHeader omits the libghostty half when the
/// library reports no version, so the degraded form reads
/// "Wintty w1.0.0-rc.1 (tip)" and stays true rather than guessing.
/// </summary>
public static class VersionBanner
{
    private static readonly object Gate = new();
    private static string? _header;

    /// <summary>
    /// "Wintty w1.0.0-rc.1 (tip) on libghostty v1.3.2-dev". Always exactly
    /// one line, which is what the rolling log's record format and every
    /// line-oriented reader of these files rely on; stable for the life of
    /// the process, so every artifact carries the identical line.
    /// </summary>
    public static string Header()
    {
        if (_header is not null) return _header;
        lock (Gate)
        {
            if (_header is not null) return _header;
            _header = Compose();
            return _header;
        }
    }

    private static string Compose()
    {
        try
        {
            return VersionHeader.Compose(VersionRenderer.Build());
        }
        catch (Exception)
        {
            // libghostty unreachable (a broken install, or a host that never
            // loaded it): render from the managed constants alone. The only
            // thing lost is the libghostty half, which VersionHeader omits
            // by itself when the library reports no version.
            return VersionHeader.Compose(ConstantsOnly());
        }
    }

    /// <summary>
    /// The <see cref="VersionRenderer.Build"/> shape, filled only from what
    /// does not need libghostty. The FFI-only fields stay empty so the
    /// header renders without them instead of inventing values.
    /// </summary>
    private static VersionInfo ConstantsOnly() => new(
        WinttyVersion:       BuildInfo.WinttyVersion,
        BuildLabel:          BuildInfo.BuildLabel,
        WinttyVersionString: BuildInfo.WinttyVersionString,
        WinttyCommit:        BuildInfo.WinttyCommit,
        Edition:             BuildInfo.Edition,
        LibGhostty:          new LibGhosttyBuildInfo(
            Version: "", VersionString: "", Commit: "",
            Channel: "", ZigVersion: "", BuildMode: ""),
        DotnetRuntime:       Environment.Version.ToString(3),
        MsbuildConfig:       BuildInfo.MsbuildConfig,
        AppRuntime:          "WinUI 3",
        Renderer:            "DX12",
        FontEngine:          "DirectWrite",
        WindowsVersion:      Environment.OSVersion.Version.ToString(3),
        Architecture:        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
}
