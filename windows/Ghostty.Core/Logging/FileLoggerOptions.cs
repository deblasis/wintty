namespace Ghostty.Core.Logging;

/// <summary>
/// Knobs for <see cref="FileLoggerProvider"/>. Path defaults to
/// <c>%LOCALAPPDATA%\Ghostty\logs</c>; size cap to 16 MB; retention to
/// 14 days. Tests inject narrower values and a temp dir.
/// </summary>
internal sealed record FileLoggerOptions
{
    public required string Directory { get; init; }
    public long MaxBytesPerFile { get; init; } = 16 * 1024 * 1024;
    public int RetentionDays { get; init; } = 14;
    public int ChannelCapacity { get; init; } = 4096;
    public int BatchMaxRecords { get; init; } = 64;
}
