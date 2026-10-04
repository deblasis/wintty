using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The gallery wrote an installed path into the user's config, so an update, a
/// move, or a different install prefix left <c>custom-shader</c> naming a file
/// that is not there -- and nothing said so, because a dead shader path and no
/// shader at all look identical from the terminal. Persisting a
/// <c>gallery:&lt;id&gt;</c> token and resolving it at use time is what the icon
/// tokens have always done; these pin the three places that decide what the
/// config holds and what libghostty is handed.
/// <para>
/// The token rules and the overlay body are Core's and are tested there
/// (ShaderGalleryTokenTests, ShaderGalleryOverlayTests). Wiring guards, because
/// the shell assembly cannot be loaded into a test host, pin the call sites:
/// the picker's commit, the page's write, and the overlay the config build
/// layers.
/// </para>
/// </summary>
public sealed class GalleryShaderTokenWiringTests
{
    private static ShellSource Picker() => ShellSource.Load("Settings.ShaderPickerWindow.xaml.cs");

    private static ShellSource AppearancePage() =>
        ShellSource.Load("Settings.Pages.AppearancePage.xaml.cs");

    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    [Fact]
    public void The_picker_commits_the_token_not_the_installed_path()
    {
        var select = Picker().Method("Select_Click");

        // Read by index rather than off the item's Tag, which still carries
        // the path the preview needs. Committing the tag is the defect, and the
        // conditional around it is what makes that readable: the committed
        // value is the token list's, never a path.
        Assert.Contains(
            "_orderedTokens[index]",
            select.AssignsTo("PickedPath").Single().Right.ToString(),
            System.StringComparison.Ordinal);
        Assert.Empty(select.CallsEndingWith("AbsolutePathFor"));
    }

    [Fact]
    public void The_picker_indexes_the_token_list_within_its_bounds()
    {
        var select = Picker().Method("Select_Click");

        // An out-of-range index must commit nothing: the tag lookup it replaced
        // could not go out of range, and a wrong entry's token is a silent
        // wrong shader.
        Assert.Contains("_orderedTokens.Count", select.ToString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_picker_preview_still_opens_a_path()
    {
        // The preview is the one consumer that needs a real path: it hands it
        // to libghostty's per-surface shader override. Null-conditional, so
        // the receiver is not part of what this asserts.
        var preview = Picker().Method("ShowPreview");

        Assert.Equal("shaderPath", preview.CallEndingWith("SetPreviewCustomShader").Arg(0));
        Assert.Empty(preview.CallsEndingWith("ResolveTokenToPath"));
    }

    [Fact]
    public void Preselecting_resolves_the_configured_value_before_comparing()
    {
        // The config can hold either form of the same entry -- the token a pick
        // now writes, or the path an older config still carries -- so the
        // comparison has to be on what both sides resolve to.
        var populate = Picker().Method("PopulateCombo");

        Assert.Equal(
            "CurrentPath",
            populate.CallEndingWith("ShaderGallery.ResolveTokenToPath").Arg(0));
        Assert.Contains("currentPath", populate.ToString(), System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_writes_the_configured_value_verbatim()
    {
        // The token has to reach the file exactly as it was picked: resolving
        // it into a path here is the whole bug. The write is one shared method
        // for the box, the browse button, and the gallery, so this covers all
        // three.
        var write = AppearancePage().Method("WriteShaderPathValue");

        Assert.Empty(write.CallsEndingWith("AbsolutePathFor"));
        Assert.Empty(write.CallsEndingWith("TokenFor"));
        Assert.Empty(write.CallsEndingWith("ResolveTokenToPath"));

        // What does reach the file is the parameter itself: the list the write
        // uses is `value` or nothing, so a token handed to this method is what
        // lands in the config.
        var values = write.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "values");
        Assert.Equal(
            "value.Length > 0 ? new[] { value } : System.Array.Empty<string>()",
            values.Initializer!.Value.ToString());
        Assert.Equal("values", write.CallEndingWith("SetRepeatableValues").Arg(1));
    }

    [Fact]
    public void The_page_reads_a_gallery_id_to_name_a_configured_token()
    {
        var sync = AppearancePage().Method("SyncShaderUiForPath");

        var isToken = sync.CallEndingWith("ShaderGallery.IsToken");
        var tryGetId = sync.CallEndingWith("ShaderGallery.TryGetTokenId");
        var byId = sync.CallEndingWith("_shaderGalleryById.TryGetValue");

        Assert.True(
            isToken.SpanStart < tryGetId.SpanStart && tryGetId.SpanStart < byId.SpanStart,
            "a configured token is classified as a file path when the id is "
            + "not parsed out of it, which sends the user looking for a file "
            + "that does not exist");
    }

    /// <summary>
    /// The half that keeps the token working at all. libghostty knows paths,
    /// so a token left in the config is a shader that cannot open -- the same
    /// silent no-shader the token was meant to end, one layer down. The build
    /// has to layer the resolved list above the user's own values.
    /// </summary>
    [Fact]
    public void Every_config_build_layers_the_resolved_shader_overlay()
    {
        var build = ConfigService().Method("BuildLiveConfig");

        var recursive = build.Call("NativeMethods.ConfigLoadRecursiveFiles");

        // This build layers three overlays (the shader one, the palette
        // preview's, High Contrast's), so the one under test is the call whose
        // argument is the shader overlay -- not merely the first
        // ConfigLoadFile, which is a coincidence of ordering.
        // config first, the file second: the shader overlay is the call whose
        // second argument is the local WriteGalleryShaderOverlay's result, as
        // opposed to the palette preview's overlayPath or High Contrast's
        // hcPath.
        var shaderOverlay = build.Calls("NativeMethods.ConfigLoadFile")
            .Single(c => c.ArgumentList.Arguments.Count == 2
                        && c.ArgumentList.Arguments[1].ToString() == "shaderOverlay");

        Assert.True(
            recursive.SpanStart < shaderOverlay.SpanStart,
            "the overlay is layered before the user's files, so the token it "
            + "clears is cleared again by them");

        // And that local is the writer's return, matched through the `is { }`
        // pattern it arrives in, so a future overlay cannot slip in behind
        // this one.
        var produced = build.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains(
                "WriteGalleryShaderOverlay", System.StringComparison.Ordinal));
        Assert.Contains(
            "shaderOverlay",
            produced.Condition.ToString(),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_overlay_is_written_from_the_users_own_values()
    {
        var write = ConfigService().Method("WriteGalleryShaderOverlay");

        Assert.Equal(
            "Ghostty.Core.Settings.ShaderGalleryOverlay.RenderFor",
            write.CallsEndingWith("RenderFor").Single().CalleeText());
    }
}
