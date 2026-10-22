using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// Source-scan wiring pins for the default-terminal startup path, per
/// the fork's wiring-test pattern: every unit-tested lifecycle behavior
/// that only matters when Program.cs calls it gets a call-site
/// assertion, because the unit suites stub the wiring and stay green
/// when the call is simply missing (that is exactly how classification,
/// the idle-embedding exit and startup revalidation shipped dead in the
/// first pass).
/// </summary>
public sealed class DeftermWiringScanTests
{
    [Fact]
    public void StartupWiring_CallSitesPresent()
    {
        var root = RepoRoot();
        var program = File.ReadAllText(
            Path.Combine(root, "windows", "Ghostty", "Program.cs"));

        // The embedding launch classifies its receiver after the
        // election instead of blindly registering a second class object.
        Assert.Contains("ClassifyReceiver(", program);

        // An embedding launch whose activation never arrives exits with
        // the idle taxonomy code instead of opening a window.
        Assert.Contains("ExitIdleEmbedding()", program);

        // Startup revalidation runs after the registration attempt and
        // its notice is surfaced.
        Assert.Contains(".Revalidate(", program);
        Assert.Contains("DefaultTermRevalidator", program);

        // The permanent registration attempt itself stays wired after
        // the election.
        Assert.Contains("TryRegisterPermanent(", program);

        // The embedding server start (the cold-activation entry), the
        // window oracle the embedding lifetime asks, and the shutdown
        // revoke-drain are all load-bearing call sites.
        Assert.Contains("StartEmbeddingServer(", program);
        Assert.Contains("RunDeftermStatus(", program);
        Assert.Contains("Shutdown()", program);
        Assert.Contains("AnyWindowExists", File.ReadAllText(
            Path.Combine(root, "windows", "Ghostty", "App.xaml.cs")));

        // The live-window pairing is structural: the shared close
        // handler is subscribed ONLY inside TrackWindowLifetime, which
        // increments in the same breath, in both files that create
        // windows. A future bare subscription or a forgotten increment
        // trips this scan instead of drifting the count silently.
        var app = File.ReadAllText(Path.Combine(root, "windows", "Ghostty", "App.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(root, "windows", "Ghostty", "MainWindow.xaml.cs"));
        var helper = app.Substring(app.IndexOf("internal void TrackWindowLifetime"));
        Assert.Contains("Interlocked.Increment(ref _liveWindows)", helper);
        var subscribeCount = System.Text.RegularExpressions.Regex.Matches(
            app + main, @"Closed \+= .*OnAnyWindowClosedInternal").Count;
        var helperCalls = System.Text.RegularExpressions.Regex.Matches(
            app + main, @"TrackWindowLifetime\(").Count;
        Assert.Equal(1, subscribeCount); // the helper's own subscription
        Assert.True(helperCalls >= 7, $"expected at least 7 helper call sites, found {helperCalls}");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "build.zig")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
