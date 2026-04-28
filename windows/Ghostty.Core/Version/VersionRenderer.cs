using System.Runtime.InteropServices;
using System.Text;

namespace Ghostty.Core.Version;

/// <summary>
/// Renders <see cref="VersionInfo"/> to either plain text (for the
/// command-palette dialog and for redirected stdout) or ANSI text (for
/// interactive stdout, which adds an OSC 8 hyperlink to the header).
/// </summary>
public static class VersionRenderer
{
    private const string CommitUrlPrefix = "https://github.com/deblasis/wintty/commit/";
    private const string Osc8Open = "\x1b]8;;";
    private const string Osc8Close = "\x1b]8;;\x1b\\";
    private const string St = "\x1b\\";
    private const int FieldValueColumn = 18;

    /// <summary>
    /// Assemble a <see cref="VersionInfo"/> from all sources: the
    /// MSBuild-generated <see cref="BuildInfo"/> constants, the libghostty
    /// FFI, the .NET runtime, and the OS.
    /// </summary>
    public static VersionInfo Build()
    {
        var lib = LibGhosttyBuildInfoBridge.Read();
        return new VersionInfo(
            WinttyVersion:       BuildInfo.WinttyVersion,
            WinttyVersionString: BuildInfo.WinttyVersionString,
            WinttyCommit:        BuildInfo.WinttyCommit,
            Edition:             BuildInfo.Edition,
            LibGhostty:          lib,
            DotnetRuntime:       Environment.Version.ToString(3),
            MsbuildConfig:       BuildInfo.MsbuildConfig,
            AppRuntime:          "WinUI 3",
            Renderer:            "DX12",
            FontEngine:          "DirectWrite",
            WindowsVersion:      Environment.OSVersion.Version.ToString(3),
            Architecture:        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }

    /// <summary>Plain rendering with header. No escape sequences. Used by
    /// redirected stdout and by the clipboard "Copy" payload in the dialog.</summary>
    public static string RenderPlain(VersionInfo info)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, info, ansi: false);
        AppendBody(sb, info);
        return sb.ToString();
    }

    /// <summary>Plain rendering of just the Version + Build Config blocks
    /// (no header, no commit URL line). The dialog uses this because the
    /// dialog's title bar already shows "Wintty &lt;version&gt;" and the
    /// commit URL is rendered as a clickable HyperlinkButton above the text,
    /// making the header redundant.</summary>
    public static string RenderPlainBody(VersionInfo info)
    {
        var sb = new StringBuilder();
        AppendBody(sb, info);
        return sb.ToString();
    }

    /// <summary>ANSI rendering. Header is wrapped in an OSC 8 hyperlink
    /// to the commit on github.com. Used for interactive stdout.</summary>
    public static string RenderAnsi(VersionInfo info)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, info, ansi: true);
        AppendBody(sb, info);
        return sb.ToString();
    }

    /// <summary>Returns the github.com commit URL for the wintty repo, or
    /// null when no commit is known. The dialog uses this to populate the
    /// HyperlinkButton; ANSI rendering uses it for the OSC 8 wrap.</summary>
    public static string? CommitUrl(VersionInfo info)
    {
        if (string.IsNullOrEmpty(info.WinttyCommit) || info.WinttyCommit == "unknown")
            return null;
        return CommitUrlPrefix + info.WinttyCommit;
    }

    private static void AppendHeader(StringBuilder sb, VersionInfo info, bool ansi)
    {
        var hasCommit = !string.IsNullOrEmpty(info.WinttyCommit) && info.WinttyCommit != "unknown";
        if (ansi && hasCommit)
        {
            sb.Append(Osc8Open).Append(CommitUrlPrefix).Append(info.WinttyCommit).Append(St);
            sb.Append("Wintty ").Append(info.WinttyVersionString);
            sb.Append(Osc8Close);
            sb.Append('\n');
        }
        else
        {
            sb.Append("Wintty ").Append(info.WinttyVersionString).Append('\n');
        }
        if (hasCommit)
        {
            sb.Append("  ").Append(CommitUrlPrefix).Append(info.WinttyCommit).Append('\n');
        }
        sb.Append('\n');
    }

    private static void AppendBody(StringBuilder sb, VersionInfo info)
    {
        sb.Append("Version\n");
        Field(sb, "version",     info.WinttyVersion);
        Field(sb, "channel",     info.LibGhostty.Channel);
        Field(sb, "edition",     EditionLabel.Format(info.Edition));
        sb.Append('\n');

        sb.Append("Build Config\n");
        Field(sb, "Zig version",  info.LibGhostty.ZigVersion);
        Field(sb, ".NET runtime", info.DotnetRuntime);
        Field(sb, "app runtime",  info.AppRuntime);
        Field(sb, "renderer",     info.Renderer);
        Field(sb, "font engine",  info.FontEngine);
        Field(sb, "libghostty",   FormatLibghosttyVersion(info.LibGhostty));
        Field(sb, "windows",      info.WindowsVersion);
        Field(sb, "arch",         info.Architecture);
        Field(sb, "build mode",   info.MsbuildConfig);
    }

    private static string FormatLibghosttyVersion(LibGhosttyBuildInfo lib)
    {
        if (string.IsNullOrEmpty(lib.Commit)) return lib.Version;
        return $"{lib.Version}+{lib.Commit}";
    }

    private static void Field(StringBuilder sb, string label, string value)
    {
        // Two-space indent, label, ":", padded to FieldValueColumn, then value.
        sb.Append("  ").Append(label).Append(':');
        var padding = FieldValueColumn - (2 + label.Length + 1);
        if (padding > 0) sb.Append(' ', padding);
        sb.Append(value).Append('\n');
    }
}
