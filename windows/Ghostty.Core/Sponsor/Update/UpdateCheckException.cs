using System;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Typed failure from an update check / download attempt. The driver
/// catches this and maps <see cref="Kind"/> to a user-visible
/// <c>ErrorMessage</c> via <c>UpdateStateMapping.FromError</c>.
/// </summary>
internal sealed class UpdateCheckException : Exception
{
    public UpdateErrorKind Kind { get; }
    public string? Detail { get; }

    public UpdateCheckException(
        UpdateErrorKind kind,
        string? detail,
        Exception? innerException = null)
        : base($"{kind}: {detail ?? "(no detail)"}", innerException)
    {
        Kind = kind;
        Detail = detail;
    }
}
