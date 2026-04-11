using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ghostty.Core.Config;

namespace Ghostty.Services;

internal sealed class ThemeProvider : IThemeProvider, IDisposable
{
    private readonly IConfigService _configService;

    public uint BackgroundColor { get; private set; } = 0xFF1E1E2E;
    public uint ForegroundColor { get; private set; } = 0xFFCDD6F4;
    public uint CursorColor { get; private set; } = 0xFFF5E0DC;
    public uint SelectionColor { get; private set; } = 0xFF585B70;
    public double BackgroundOpacity { get; private set; } = 1.0;
    public string? FontFamily { get; private set; }
    public double FontSize { get; private set; } = 13.0;
    public string? ThemeName { get; private set; }
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
        // Enumerate theme files from the user themes directory.
        // Ghostty looks for themes in <config_dir>/themes/<name>.
        var configDir = Path.GetDirectoryName(_configService.ConfigFilePath);
        if (string.IsNullOrEmpty(configDir))
        {
            AvailableThemes = Array.Empty<string>();
            return;
        }

        var themesDir = Path.Combine(configDir, "themes");
        if (!Directory.Exists(themesDir))
        {
            AvailableThemes = Array.Empty<string>();
            return;
        }

        // Each file in the themes directory is a theme. The filename
        // (without extension) is the theme name.
        AvailableThemes = Directory.EnumerateFiles(themesDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }
}
