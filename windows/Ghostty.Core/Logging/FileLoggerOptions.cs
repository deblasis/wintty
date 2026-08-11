namespace Ghostty.Core.Logging;

/// <summary>
/// Knobs for <see cref="FileLoggerProvider"/>. Path defaults to
/// <c>%LOCALAPPDATA%\Wintty\logs</c>; size cap to 16 MB; retention to
/// 14 days. Tests inject narrower values and a temp dir.
/// </summary>
internal sealed record FileLoggerOptions
{
    public required string Directory { get; init; }
    public long MaxBytesPerFile { get; init; } = 16 * 1024 * 1024;
    public int RetentionDays { get; init; } = 14;
    public int ChannelCapacity { get; init; } = 4096;
    public int BatchMaxRecords { get; init; } = 64;

    /// <summary>
    /// Hard ceiling on the combined size of all <c>ghostty-*.log</c> files
    /// in the directory. Date-based <see cref="RetentionDays"/> pruning
    /// cannot bound a same-day storm: a runaway producer rolls thousands of
    /// today-dated files that no date cutoff will ever delete. This cap is
    /// enforced on every rollover by deleting the oldest files first, so a
    /// logging fault in any component can never fill the disk. Default
    /// 512 MB; normal sessions log a few MB/day and never approach it.
    /// </summary>
    public long MaxTotalBytes { get; init; } = 512L * 1024 * 1024;
}
