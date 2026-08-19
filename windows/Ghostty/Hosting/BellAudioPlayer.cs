using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Ghostty.Hosting;

/// <summary>
/// Plays the configured bell audio file via a WinRT <see cref="MediaPlayer"/>.
/// One instance per surface (owned by <c>TerminalControl</c>). The
/// <see cref="MediaPlayer"/> and its source are rebuilt only when the path
/// changes, mirroring the GTK apprt which reuses one media file per surface.
/// Failures (missing/unreadable/unsupported file) are logged and swallowed:
/// a broken audio path must never crash or block the rest of the bell.
/// </summary>
internal sealed partial class BellAudioPlayer : IDisposable
{
    private readonly ILogger _logger;
    private MediaPlayer? _player;
    private string? _currentPath;

    /// <summary>
    /// Absolute path to the bundled default bell (Assets/bell.wav, deployed
    /// next to the app), or null if it isn't present. Used as the fallback
    /// when <c>bell-features</c> enables <c>audio</c> but the user has not set
    /// <c>bell-audio-path</c>. Resolved once; the path is fixed per install.
    /// </summary>
    internal static string? BundledDefaultPath { get; } = ResolveBundledDefault();

    private static string? ResolveBundledDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "bell.wav");
        return File.Exists(path) ? path : null;
    }

    public BellAudioPlayer(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void Play(string path, double volume)
    {
        try
        {
            _player ??= new MediaPlayer
            {
                // Mixes/ducks as a short notification rather than media.
                AudioCategory = MediaPlayerAudioCategory.SoundEffects,
            };

            if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _player.Source = MediaSource.CreateFromUri(new Uri(path));
                _currentPath = path;
            }

            _player.Volume = Math.Clamp(volume, 0.0, 1.0);
            // Rewind so rapid bells replay from the start instead of being
            // dropped while a prior play is still in flight.
            if (_player.PlaybackSession is { } session)
                session.Position = TimeSpan.Zero;
            _player.Play();
        }
        catch (Exception ex)
        {
            // Cold error path; plain logging is acceptable and AOT-safe.
            _logger.LogWarning(ex, "bell audio playback failed for {Path}", path);
        }
    }

    public void Dispose()
    {
        _player?.Dispose();
        _player = null;
        _currentPath = null;
    }
}
