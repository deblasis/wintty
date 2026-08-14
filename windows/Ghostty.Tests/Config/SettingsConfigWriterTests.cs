using System;
using System.Collections.Generic;
using System.IO;
using Ghostty.Core.Config;
using Ghostty.Core.Profiles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Config;

public class SettingsConfigWriterTests
{
    [Fact]
    public void Write_runs_edit_then_reloads_and_reports_success()
    {
        var svc = new FakeConfigService { ReloadResult = true };
        var writer = new SettingsConfigWriter(svc, NullLogger<SettingsConfigWriter>.Instance);

        var edited = false;
        var result = writer.Write(() => edited = true);

        Assert.True(edited);
        Assert.True(result.WriteSucceeded);
        Assert.True(result.Reloaded);
        Assert.Null(result.Error);
        Assert.Equal(1, svc.ReloadCount);
    }

    [Fact]
    public void Write_brackets_edit_with_suppress_true_then_false()
    {
        var svc = new FakeConfigService();
        var writer = new SettingsConfigWriter(svc, NullLogger<SettingsConfigWriter>.Instance);

        writer.Write(() => svc.SuppressLog.Add("edit"));

        // Watcher is suppressed before the edit runs and resumed after.
        Assert.Equal(new[] { "true", "edit", "false" }, svc.SuppressLog);
    }

    [Fact]
    public void Write_returns_reload_result_when_reload_fails()
    {
        var svc = new FakeConfigService { ReloadResult = false };
        var writer = new SettingsConfigWriter(svc, NullLogger<SettingsConfigWriter>.Instance);

        var result = writer.Write(() => { });

        Assert.True(result.WriteSucceeded);
        Assert.False(result.Reloaded);
    }

    [Fact]
    public void Write_swallows_IOException_and_still_resets_watcher_and_reloads()
    {
        var svc = new FakeConfigService { ReloadResult = true };
        var writer = new SettingsConfigWriter(svc, NullLogger<SettingsConfigWriter>.Instance);
        var boom = new IOException("disk full");

        var result = writer.Write(() => throw boom);

        Assert.False(result.WriteSucceeded);
        Assert.Same(boom, result.Error);
        // Watcher must be balanced even on failure, and the reload still runs
        // so the runtime resyncs to whatever actually landed on disk.
        Assert.Equal(new[] { "true", "false" }, svc.SuppressLog);
        Assert.Equal(1, svc.ReloadCount);
    }

    [Fact]
    public void Write_swallows_UnauthorizedAccessException()
    {
        var svc = new FakeConfigService();
        var writer = new SettingsConfigWriter(svc, NullLogger<SettingsConfigWriter>.Instance);
        var boom = new UnauthorizedAccessException("denied");

        var result = writer.Write(() => throw boom);

        Assert.False(result.WriteSucceeded);
        Assert.Same(boom, result.Error);
    }

    [Fact]
    public void Write_does_not_swallow_unexpected_exceptions_but_still_resets_watcher()
    {
        var svc = new FakeConfigService();
        var writer = new SettingsConfigWriter(svc, NullLogger<SettingsConfigWriter>.Instance);

        // A programming error (not a disk failure) must propagate so it isn't
        // silently masked -- but the watcher flag must still be balanced.
        Assert.Throws<InvalidOperationException>(
            () => writer.Write(() => throw new InvalidOperationException("bug")));

        Assert.Equal(new[] { "true", "false" }, svc.SuppressLog);
        Assert.Equal(0, svc.ReloadCount); // reload is skipped when the edit faults unexpectedly
    }

    [Fact]
    public void Constructor_rejects_null_arguments()
    {
        var svc = new FakeConfigService();
        Assert.Throws<ArgumentNullException>(
            () => new SettingsConfigWriter(null!, NullLogger<SettingsConfigWriter>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new SettingsConfigWriter(svc, null!));
    }

    [Fact]
    public void Write_rejects_null_edit()
    {
        var svc = new FakeConfigService();
        var writer = new SettingsConfigWriter(svc, NullLogger<SettingsConfigWriter>.Instance);
        Assert.Throws<ArgumentNullException>(() => writer.Write(null!));
    }

    private sealed class FakeConfigService : IConfigService
    {
        public List<string> SuppressLog { get; } = new();
        public int ReloadCount { get; private set; }
        public bool ReloadResult { get; set; } = true;

        public void SuppressWatcher(bool suppress) => SuppressLog.Add(suppress ? "true" : "false");
        public bool Reload() { ReloadCount++; return ReloadResult; }

        // --- Unused members (the writer only touches SuppressWatcher/Reload) ---
        public event Action<IConfigService>? ConfigChanged { add { } remove { } }
        public string ConfigFilePath => string.Empty;
        public bool AutoReloadEnabled => false;
        public bool SettingsUiEnabled => false;
        public double BackgroundOpacity => 1.0;
        public bool VerticalTabs => false;
        public bool CommandPaletteGroupCommands => false;
        public bool WindowsHighContrast => false;
        public void SetHighContrastOverride(string? body) { }
        public string CommandPaletteBackground => "acrylic";
        public string NoColorOverride => "notify";
        public string LogLevel => "info";
        public string LogFilter => string.Empty;
        public string WindowTheme => "auto";
        public uint BackgroundColor => 0;
        public int UndoTimeoutMs => 5000;
        public Ghostty.Core.Bell.BellFeatures BellFeatures => default;
        public string? BellAudioPath => null;
        public double BellAudioVolume => 0.5;
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
