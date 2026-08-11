namespace Ghostty.Core.Version;

/// <summary>
/// Static, AOT-safe strings and links rendered by the About window. Kept
/// in Core (not the WinUI shell) so the values are unit-testable without a
/// UI thread. Version/build/commit come from <see cref="VersionRenderer"/>;
/// only the brand-level copy and project links live here.
/// </summary>
public static class AboutContent
{
    public const string Tagline =
        "Fast, native, feature-rich terminal emulator pushing modern features.";

    public const string LicenseNote = "MIT License";

    // Two lines: Wintty's own copyright for the Windows port and its added
    // features, then the upstream Ghostty notice retained verbatim as MIT
    // requires. Only the "Based on Ghostty" line is pinned to the repo LICENSE
    // and changes when that file does; the Wintty line is this port's own copy.
    // The embedded '\n' is intentional: the About TextBlock renders it as a
    // line break, keeping this value UI-free and unit-testable here in Core.
    public const string Copyright =
        AppIdentity.ProductName + " (c) 2026 Alessandro De Blasis\n" +
        "Based on Ghostty (c) 2024 Mitchell Hashimoto, Ghostty contributors";

    public const string GitHubUrl = "https://github.com/deblasis/wintty";
    public const string DocsUrl = "https://wintty.io/docs";
    public const string HomepageUrl = "https://wintty.io";
    public const string SponsorUrl = "https://github.com/sponsors/deblasis";
}
