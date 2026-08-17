using Ghostty.Core.Notifications;

namespace Ghostty.Core.Inspector;

/// <summary>
/// Copy for the in-window banner when Toggle Inspector cannot open a
/// surface. UI-free so the wording unit-tests without WinUI, same as
/// <c>CustomShaderNoticeSource</c> / <c>NoColorStartup</c>.
/// </summary>
public static class InspectorNotice
{
    public const string DedupKey = "inspector-dx12-unimplemented";

    public static Notice Dx12Unimplemented() => new()
    {
        Title = "Inspector unavailable",
        Message = "The DirectX 12 inspector surface backend is not implemented yet. "
            + "Toggle Inspector will no-op until that backend exists.",
        Severity = NoticeSeverity.Informational,
        DedupKey = DedupKey,
    };
}
