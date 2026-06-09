using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Config;
using Ghostty.Core.Logging;
using Ghostty.Core.Logging.Testing;
using Ghostty.Core.Profiles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ghostty.Tests.Logging;

public class SettingsConfigWriterLoggingTests
{
    [Fact]
    public void Write_EditThatThrowsIOException_EmitsWarningWithSettingsWriteErrEventId()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var writer = new SettingsConfigWriter(
            new NoopConfigService(),
            factory.CreateLogger<SettingsConfigWriter>());

        writer.Write(() => throw new System.IO.IOException("injected"));

        var warnings = capture.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
        Assert.Contains(warnings, e => e.EventId.Id == LogEvents.Config.SettingsWriteErr);
    }

    [Fact]
    public void Write_WithContext_RendersContextInWarningMessage()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        var writer = new SettingsConfigWriter(
            new NoopConfigService(),
            factory.CreateLogger<SettingsConfigWriter>());

        writer.Write(() => throw new System.IO.IOException("injected"), "background-opacity");

        var warning = capture.Entries.Single(
            e => e.Level == LogLevel.Warning && e.EventId.Id == LogEvents.Config.SettingsWriteErr);
        Assert.Contains("background-opacity", warning.Message);
    }

    // Minimal IConfigService stand-in: the writer only calls
    // SuppressWatcher and Reload, so everything else is a default no-op.
    private sealed class NoopConfigService : IConfigService
    {
        public void SuppressWatcher(bool suppress) { }
        public bool Reload() => true;

        public event Action<IConfigService>? ConfigChanged { add { } remove { } }
        public string ConfigFilePath => string.Empty;
        public bool AutoReloadEnabled => false;
        public bool SettingsUiEnabled => false;
        public double BackgroundOpacity => 1.0;
        public bool VerticalTabs => false;
        public bool CommandPaletteGroupCommands => false;
        public string CommandPaletteBackground => "acrylic";
        public string LogLevel => "info";
        public string LogFilter => string.Empty;
        public string WindowTheme => "auto";
        public uint BackgroundColor => 0;
        public int UndoTimeoutMs => 5000;
        public int DiagnosticsCount => 0;
        public string GetDiagnostic(int index) => string.Empty;
        public IReadOnlyList<string> WindowsOnlyKeysUsed => Array.Empty<string>();
        public IReadOnlyDictionary<string, ProfileDef> ParsedProfiles =>
            new Dictionary<string, ProfileDef>();
        public IReadOnlyList<string> ProfileOrder => Array.Empty<string>();
        public string? DefaultProfileId => null;
        public IReadOnlySet<string> HiddenProfileIds => new HashSet<string>();
        public IReadOnlyList<string> ProfileWarnings => Array.Empty<string>();
        public void Dispose() { }
    }
}
