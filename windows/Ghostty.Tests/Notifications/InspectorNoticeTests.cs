using Ghostty.Core.Inspector;
using Ghostty.Core.Notifications;
using Xunit;

namespace Ghostty.Tests.Notifications;

/// <summary>
/// Toggle Inspector currently no-ops (handle zero, or the window closes
/// after DX12 surface init returns false). The only trace was a Zig
/// log.warn. This notice is the in-window explanation, matching
/// CustomShaderNoticeSource.
/// </summary>
public class InspectorNoticeTests
{
    [Fact]
    public void Dx12Unimplemented_IsInformationalWithDedupKey()
    {
        var notice = InspectorNotice.Dx12Unimplemented();
        Assert.Equal("Inspector unavailable", notice.Title);
        Assert.Contains("DirectX 12", notice.Message, System.StringComparison.Ordinal);
        Assert.Equal(NoticeSeverity.Informational, notice.Severity);
        Assert.Equal(InspectorNotice.DedupKey, notice.DedupKey);
        Assert.True(notice.IsClosable);
    }
}
