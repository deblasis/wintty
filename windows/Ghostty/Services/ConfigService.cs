using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Ghostty.Core.Config;
using Ghostty.Interop;
using Microsoft.UI.Dispatching;

namespace Ghostty.Services;

/// <summary>
/// Owns the libghostty config lifecycle: init, load from disk,
/// reload, and file-system watching (behind <c>auto-reload-config</c>).
/// Fires <see cref="ConfigChanged"/> on the UI thread after every
/// successful reload so consumers can re-read values they depend on.
/// </summary>
internal sealed class ConfigService : IConfigService
{
    private GhosttyConfig _config;
    private GhosttyApp _app;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private readonly Lock _timerLock = new();
    private readonly DispatcherQueue _dispatcher;
    private volatile bool _suppressWatcher;

    public event Action<IConfigService>? ConfigChanged;
    public string ConfigFilePath { get; }
    public bool AutoReloadEnabled { get; private set; }
    public bool SettingsUiEnabled { get; private set; }
    public int DiagnosticsCount { get; private set; }

    /// <summary>
    /// The current config handle. Passed to <see cref="GhosttyHost"/>
    /// so it can create the app with the loaded config.
    /// </summary>
    public GhosttyConfig ConfigHandle => _config;

    public ConfigService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        NativeMethods.Init(UIntPtr.Zero, IntPtr.Zero);

        _config = NativeMethods.ConfigNew();
        NativeMethods.ConfigLoadDefaultFiles(_config);
        NativeMethods.ConfigFinalize(_config);

        var pathStr = NativeMethods.ConfigOpenPath();
        var rawPath = pathStr.Ptr != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(pathStr.Ptr, (int)pathStr.Len) ?? string.Empty
            : string.Empty;
        // Normalize mixed separators from Zig (forward slash) + Windows
        // (backslash) so the path looks clean in UI and logs.
        ConfigFilePath = Path.GetFullPath(rawPath);

        CacheDiagnostics();
        ReadFlags();
    }

    /// <summary>
    /// Must be called after <c>ghostty_app_new()</c> so reloads can
    /// push the new config into the running app via
    /// <c>ghostty_app_update_config</c>.
    /// </summary>
    public void SetApp(GhosttyApp app)
    {
        _app = app;
        if (AutoReloadEnabled) StartWatcher();
    }

    public bool Reload()
    {
        // Don't reload before the app is created -- the initial config
        // is the one passed to ghostty_app_new and must stay alive.
        if (_app.Handle == IntPtr.Zero) return false;

        GhosttyConfig newConfig;
        try
        {
            newConfig = NativeMethods.ConfigNew();
            NativeMethods.ConfigLoadDefaultFiles(newConfig);
            NativeMethods.ConfigFinalize(newConfig);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ConfigService] Reload failed to create new config: {ex.Message}");
            return false;
        }

        var oldConfig = _config;

        // Suppress the watcher for the duration of the update so our
        // own config swap doesn't trigger a redundant file-change reload.
        var wasSuppressed = _suppressWatcher;
        _suppressWatcher = true;

        NativeMethods.AppUpdateConfig(_app, newConfig);

        _config = newConfig;
        CacheDiagnostics();
        ReadFlags();

        if (oldConfig.Handle != IntPtr.Zero)
            NativeMethods.ConfigFree(oldConfig);

        _suppressWatcher = wasSuppressed;

        _dispatcher.TryEnqueue(() => ConfigChanged?.Invoke(this));
        return true;
    }

    public string GetDiagnostic(int index)
    {
        if (index < 0 || index >= DiagnosticsCount) return string.Empty;
        var diag = NativeMethods.ConfigGetDiagnostic(_config, (uint)index);
        return diag.Message != IntPtr.Zero
            ? Marshal.PtrToStringUTF8(diag.Message) ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// Temporarily suppress or resume file-system watcher events.
    /// Used by <see cref="ConfigFileEditor"/> during writes so our
    /// own save does not trigger a redundant reload.
    /// </summary>
    public void SuppressWatcher(bool suppress) => _suppressWatcher = suppress;

    private void CacheDiagnostics()
    {
        DiagnosticsCount = (int)NativeMethods.ConfigDiagnosticsCount(_config);
    }

    private void ReadFlags()
    {
        AutoReloadEnabled = GetBool("auto-reload-config");
        SettingsUiEnabled = GetBool("windows-settings-ui");
    }

    private unsafe bool GetBool(string key)
    {
        byte result = 0;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        fixed (byte* keyPtr = keyBytes)
        {
            var found = NativeMethods.ConfigGet(
                _config,
                (IntPtr)(&result),
                (IntPtr)keyPtr,
                (UIntPtr)keyBytes.Length);
            return found && result != 0;
        }
    }

    private void StartWatcher()
    {
        if (_watcher != null) return;
        var dir = Path.GetDirectoryName(ConfigFilePath);
        var file = Path.GetFileName(ConfigFilePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;
        if (!Directory.Exists(dir)) return;

        _watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += (s, e) => OnFileChanged(s, e);
    }

    private void StopWatcher()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        if (_suppressWatcher) return;
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                _dispatcher.TryEnqueue(() => Reload());
            }, null, 300, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        StopWatcher();
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
        }
        if (_config.Handle != IntPtr.Zero)
            NativeMethods.ConfigFree(_config);
    }
}
