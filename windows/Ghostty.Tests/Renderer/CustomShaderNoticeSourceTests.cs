using Ghostty.Core.Notifications;
using Ghostty.Core.Renderer;
using Xunit;

namespace Ghostty.Tests.Renderer;

public class CustomShaderNoticeSourceTests
{
    [Fact]
    public void First_failure_returns_a_closable_warning_with_no_actions()
    {
        var notice = new CustomShaderNoticeSource().Resolve(CustomShaderFailure.CompilerUnavailable);

        Assert.NotNull(notice);
        Assert.Equal("Custom shader not applied", notice!.Title);
        Assert.Equal(NoticeSeverity.Warning, notice.Severity);
        Assert.Equal(CustomShaderNoticeSource.DedupKey, notice.DedupKey);
        Assert.Empty(notice.Actions);
        Assert.True(notice.IsClosable);
    }

    [Theory]
    [InlineData(CustomShaderFailure.LoadFailed)]
    [InlineData(CustomShaderFailure.CompilerUnavailable)]
    [InlineData(CustomShaderFailure.CompileFailed)]
    [InlineData(CustomShaderFailure.PipelineFailed)]
    public void Every_failure_has_its_own_copy(CustomShaderFailure failure)
    {
        var notice = new CustomShaderNoticeSource().Resolve(failure);

        Assert.NotNull(notice);
        Assert.NotEqual(string.Empty, notice!.Message);
    }

    [Theory]
    [InlineData(CustomShaderFailure.LoadFailed)]
    [InlineData(CustomShaderFailure.CompilerUnavailable)]
    [InlineData(CustomShaderFailure.CompileFailed)]
    [InlineData(CustomShaderFailure.PipelineFailed)]
    public void Copy_never_claims_the_shader_is_working(CustomShaderFailure failure)
    {
        // The notice must describe a shader that did nothing and a terminal
        // that is otherwise fine. Promising more than that is the one thing
        // this copy cannot do.
        var notice = new CustomShaderNoticeSource().Resolve(failure);

        Assert.Contains("renders normally without it", notice!.Message);
        Assert.True(
            notice.Message.Contains("skipped") || notice.Message.Contains("no effect"),
            $"copy should say the shader is not being applied: {notice.Message}");
    }

    [Fact]
    public void CompilerUnavailable_names_the_missing_dll_so_a_bug_report_can_use_it()
    {
        var notice = new CustomShaderNoticeSource().Resolve(CustomShaderFailure.CompilerUnavailable);

        Assert.Contains("dxcompiler.dll", notice!.Message);
    }

    [Fact]
    public void Repeat_of_the_same_failure_is_suppressed()
    {
        // One config reload re-inits every surface's shaders, so N panes
        // produce N actions for a single user-visible event.
        var source = new CustomShaderNoticeSource();

        Assert.NotNull(source.Resolve(CustomShaderFailure.CompileFailed));
        Assert.Null(source.Resolve(CustomShaderFailure.CompileFailed));
        Assert.Null(source.Resolve(CustomShaderFailure.CompileFailed));
    }

    [Fact]
    public void A_different_failure_re_arms_the_notice()
    {
        // The user fixed a syntax error and now hits a pipeline failure: that
        // is new information, not a repeat.
        var source = new CustomShaderNoticeSource();

        Assert.NotNull(source.Resolve(CustomShaderFailure.CompileFailed));
        Assert.NotNull(source.Resolve(CustomShaderFailure.PipelineFailed));
    }
}
