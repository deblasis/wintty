#if DEBUG
using System.Collections.Generic;
using Ghostty.Commands;
using Ghostty.Core.Sponsor.Update;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Debug-only command source that exposes every simulator transition as
/// a palette entry under the "Custom" category. Exists only so manual
/// QA can drive the update UI without keyboard shortcuts. Production
/// sponsor builds compile this out via <c>#if DEBUG</c>.
/// </summary>
internal sealed class SponsorUpdateCommandSource : ICommandSource
{
    private readonly UpdateSimulator _simulator;
    private readonly List<CommandItem> _items;

    public SponsorUpdateCommandSource(UpdateSimulator simulator)
    {
        _simulator = simulator;
        _items = Build();
    }

    public IReadOnlyList<CommandItem> GetCommands() => _items;
    public void Refresh() { /* static set */ }

    private List<CommandItem> Build()
    {
        var list = new List<CommandItem>();
        void Add(string id, string title, UpdateState state,
                 string? version = null, double? progress = null, string? error = null,
                 string? releaseNotesUrl = null)
        {
            list.Add(new CommandItem
            {
                Id = id,
                Title = title,
                Description = "Drive the update simulator for manual QA.",
                Category = CommandCategory.Custom,
                Execute = _ => _simulator.Simulate(state, version, progress, error, releaseNotesUrl),
            });
        }

        const string releasesBase = "https://github.com/deblasis/wintty/releases/tag/";

        Add("sponsor.sim.idle",            "Simulate: Idle",                       UpdateState.Idle);
        Add("sponsor.sim.no-updates",      "Simulate: No Updates",                 UpdateState.NoUpdatesFound);
        Add("sponsor.sim.available",       "Simulate: Update Available (1.4.2)",   UpdateState.UpdateAvailable,
            version: "1.4.2", releaseNotesUrl: releasesBase + "v1.4.2");
        // Separate palette entry with a newer version so QA can verify
        // that Skip on 1.4.2 does NOT suppress 1.5.0 (per-version skip).
        Add("sponsor.sim.available-new",   "Simulate: Update Available (1.5.0)",   UpdateState.UpdateAvailable,
            version: "1.5.0", releaseNotesUrl: releasesBase + "v1.5.0");
        Add("sponsor.sim.downloading",     "Simulate: Downloading 42%",            UpdateState.Downloading, progress: 0.42);
        Add("sponsor.sim.extracting",      "Simulate: Extracting",                 UpdateState.Extracting);
        Add("sponsor.sim.installing",      "Simulate: Installing",                 UpdateState.Installing);
        Add("sponsor.sim.restart-pending", "Simulate: Restart Pending",            UpdateState.RestartPending,
            version: "1.4.2", releaseNotesUrl: releasesBase + "v1.4.2");
        Add("sponsor.sim.error",           "Simulate: Error",                      UpdateState.Error, error: "Simulated failure");
        return list;
    }
}
#endif
