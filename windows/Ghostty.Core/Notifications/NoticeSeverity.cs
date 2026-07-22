namespace Ghostty.Core.Notifications;

/// <summary>
/// Visual severity of a <see cref="Notice"/>. Maps 1:1 to WinUI's
/// InfoBarSeverity at the render layer; kept as a Core enum so the notice
/// model has no UI dependency and stays unit-testable.
/// </summary>
public enum NoticeSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}
