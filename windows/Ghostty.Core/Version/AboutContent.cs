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

    // Year and holders are pinned to the repo LICENSE file, not the current
    // date: this is the copyright on the work, so it tracks LICENSE and only
    // changes when that file does.
    public const string Copyright = "(c) 2024 Mitchell Hashimoto, Ghostty contributors";

    public const string GitHubUrl = "https://github.com/deblasis/wintty";
    public const string DocsUrl = "https://wintty.io/docs";
    public const string HomepageUrl = "https://wintty.io";
    public const string SponsorUrl = "https://github.com/sponsors/deblasis";
}
