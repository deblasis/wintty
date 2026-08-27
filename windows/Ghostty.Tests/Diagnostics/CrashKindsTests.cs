using System;
using System.Linq;
using System.Text.RegularExpressions;
using Ghostty.Core.Diagnostics;
using Xunit;

namespace Ghostty.Tests.Diagnostics;

/// <summary>
/// The crash-trigger catalogue, which the CLI (<c>+crash</c>) and the
/// command palette both read.
///
/// Nothing here runs a trigger, and nothing here can: the catalogue is
/// metadata and the mechanisms live in the shell assembly, which no test
/// host loads. That separation is deliberate rather than a limitation.
/// It is what lets the shape of the set be asserted against the real list
/// in a process that is not allowed to die.
/// </summary>
public class CrashKindsTests
{
    [Fact]
    public void Catalogue_IsNotEmpty()
    {
        // Load-bearing: every other test here reads "nothing to check" out
        // of an empty list and passes.
        Assert.NotEmpty(CrashKinds.All);
    }

    [Fact]
    public void Ids_AreUniqueAndWireShaped()
    {
        var ids = CrashKinds.All.Select(k => k.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        foreach (var id in ids)
        {
            // These are typed on a command line and stored as frecency keys.
            // Kebab-case lowercase matches the CLI's other multi-word
            // spellings and leaves no room for a casing variant that only
            // works on one of the two front doors.
            Assert.Matches(new Regex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$"), id);
        }
    }

    [Fact]
    public void EveryTitle_SaysItIsADebugAction()
    {
        // The palette sorts by frecency, so a crash trigger can surface next
        // to New Tab. The prefix is the only thing standing between a
        // developer and an accidental fail-fast, and it has to be in the
        // title: the palette renders Title and Description and nothing else
        // (Badge and Emphasis on CommandItem are unbound today).
        foreach (var kind in CrashKinds.All)
        {
            Assert.StartsWith("Debug: ", kind.Title, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(kind.Description));
        }
    }

    [Fact]
    public void NeedsSurface_IsExactlyTheBindingActionKinds()
    {
        // NeedsSurface is what the CLI refuses on. If it could be true for a
        // kind with no binding action, +crash would start refusing a kind it
        // can perfectly well run.
        foreach (var kind in CrashKinds.All)
            Assert.Equal(kind.BindingAction is not null, kind.NeedsSurface);
    }

    [Fact]
    public void BindingActions_AreTheCrashActionLibghosttyDefines()
    {
        // libghostty's `crash` binding action takes a CrashThread value:
        // main, io or render (src/input/Binding.zig). Anything else is
        // silently rejected by ghostty_surface_binding_action, which the
        // trigger reports as "not accepted" rather than as a crash, so the
        // failure is loud but only at runtime. Pin the spellings here.
        var actions = CrashKinds.All
            .Where(k => k.BindingAction is not null)
            .Select(k => k.BindingAction!)
            .ToList();

        Assert.NotEmpty(actions);
        Assert.Equal(actions.Count, actions.Distinct(StringComparer.Ordinal).Count());
        foreach (var action in actions)
            Assert.Contains(action, new[] { "crash:main", "crash:io", "crash:render" });
    }

    [Fact]
    public void EveryLayer_IsCovered()
    {
        // The point of the matrix is one probe per capture boundary. A layer
        // with no kind is a row nobody can run.
        foreach (var layer in Enum.GetValues<CrashLayer>())
            Assert.Contains(CrashKinds.All, k => k.Layer == layer);
    }

    [Fact]
    public void TheRendererKind_IsNotTiedToABackend()
    {
        // The renderer probe faults on the shared render thread
        // (src/renderer/Thread.zig), above the DirectX12 / DirectX11 split,
        // so the pro-legacy DX11 tier gets it without a tier-specific patch.
        // A kind named after a backend is the regression: it would have to
        // be forked per tier.
        var renderer = Assert.Single(CrashKinds.All.Where(k => k.Layer == CrashLayer.Renderer));
        foreach (var backend in new[] { "dx11", "dx12", "directx", "d3d", "metal", "opengl" })
        {
            Assert.DoesNotContain(backend, renderer.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(backend, renderer.Title, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExactlyOneKind_IsDeliberatelyNotACrash()
    {
        // handled-storm measures that a flood of caught exceptions produces
        // no report. A second non-crashing kind is fine in principle, but it
        // has not been thought about; making it fail here is cheaper than
        // discovering that a probe named "crash" never crashed.
        Assert.Single(CrashKinds.All.Where(k => !k.Crashes));
    }

    [Fact]
    public void Find_MatchesOrdinallyAndExactly()
    {
        var first = CrashKinds.All[0];
        Assert.Same(first, CrashKinds.Find(first.Id));
        Assert.Null(CrashKinds.Find("no-such-kind"));
        Assert.Null(CrashKinds.Find(""));
        // Case-sensitive on purpose: an id is a wire spelling, and a loose
        // compare is how a locale-dependent uppercase starts matching.
        Assert.Null(CrashKinds.Find(first.Id.ToUpperInvariant()));
    }

    [Fact]
    public void Ids_ListsEveryKind()
    {
        // The CLI prints this when it is handed an unknown kind, so a kind
        // missing from it is a kind nobody can discover.
        var listed = CrashKinds.Ids.Split(", ");
        Assert.Equal(CrashKinds.All.Select(k => k.Id).ToArray(), listed);
    }
}
