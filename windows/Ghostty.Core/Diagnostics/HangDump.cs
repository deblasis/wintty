using System;
using Ghostty.Core.Config;

namespace Ghostty.Core.Diagnostics;

/// <summary>
/// Capture scope of the hang watchdog's minidump, selected by the
/// <c>hang-dump</c> config key.
/// </summary>
public enum HangDumpMode
{
    /// <summary>
    /// Stacks, handles and lock words without the heap sweep: small
    /// enough to attach to a bug report and free of terminal content
    /// and secrets. The default.
    /// </summary>
    Triage,

    /// <summary>
    /// All process memory. Opt-in only: a full dump carries everything
    /// the terminals held, secrets included, and is correspondingly
    /// large.
    /// </summary>
    Full,
}

/// <summary>
/// Values and parsing for the <c>hang-dump</c> config key, shaped after
/// <c>NoColorPolicy</c> so every allowed-string key resolves the same
/// way: unset and invalid values both land on the default, silently,
/// which is <see cref="WindowsOnlyKeyParsers.ParseStringAllowed"/>'s
/// convention.
/// </summary>
public static class HangDump
{
    public const string Triage = "triage";

    public const string Full = "full";

    /// <summary>Allowed values for the <c>hang-dump</c> config key.</summary>
    public static readonly string[] Allowed = { Triage, Full };

    /// <summary>Default when the key is unset or invalid.</summary>
    public const string Default = Triage;

    public static HangDumpMode Parse(string? raw) =>
        WindowsOnlyKeyParsers.ParseStringAllowed(raw, Allowed, Default) == Full
            ? HangDumpMode.Full
            : HangDumpMode.Triage;

    /// <summary>Config spelling of a mode, for crash.log lines.</summary>
    public static string ConfigValue(HangDumpMode mode) =>
        mode == HangDumpMode.Full ? Full : Triage;
}
