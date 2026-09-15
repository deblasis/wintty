using System;
using System.Collections.Generic;
using System.IO;
using Ghostty.Core.Config;
using Ghostty.Core.Themes;

namespace Ghostty.Services;

internal sealed partial class ThemeProvider : IThemeProvider, IDisposable
{
    private readonly IConfigService _configService;

    public IReadOnlyList<string> AvailableThemes { get; private set; } = Array.Empty<string>();

    public ThemeProvider(IConfigService configService)
    {
        _configService = configService;
        _configService.ConfigChanged += OnConfigChanged;
        Refresh();
    }

    public void Dispose()
    {
        _configService.ConfigChanged -= OnConfigChanged;
    }

    private void OnConfigChanged(IConfigService _) => Refresh();

    private void Refresh()
    {
        // The same list the command palette's theme mode offers, from the
        // same directories libghostty resolves a configured theme in, so the
        // two pickers cannot disagree about which themes exist.
        AvailableThemes = ThemeCatalog.Enumerate(Directories(_configService.ConfigFilePath));
    }

    /// <summary>
    /// The theme directories for a config file path, in libghostty's search
    /// order. Shared with the palette so both enumerate one list.
    /// </summary>
    internal static IEnumerable<string> Directories(string configFilePath)
        => ThemeSearchPath.UserDirectories(
            Path.GetDirectoryName(configFilePath),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
}
