using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Update;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Adds or removes an "Install Pending Update" jump-list task based on
/// state. The item's argument invokes wintty://update?action=install-restart
/// which the SponsorActivationRouter dispatches on launch.
/// </summary>
internal sealed class UpdateJumpListProvider : IDisposable
{
    private const string UpdateTaskArg = "--uri wintty://update?action=install-restart";
    private const string UpdateTaskLabel = "Install Pending Update";
    private readonly UpdateService _service;

    public UpdateJumpListProvider(UpdateService service)
    {
        _service = service;
    }

    public void Attach()
    {
        _service.StateChanged += OnStateChanged;
        _ = SyncAsync(_service.Current.State);
    }

    private void OnStateChanged(object? sender, UpdateStateSnapshot snap)
    {
        _ = SyncAsync(snap.State);
    }

    private static async Task SyncAsync(UpdateState state)
    {
        try
        {
            if (!Windows.UI.StartScreen.JumpList.IsSupported()) return;
            var jl = await Windows.UI.StartScreen.JumpList.LoadCurrentAsync();
            var removedByUser = jl.Items.Any(i => i.Arguments == UpdateTaskArg && i.RemovedByUser);
            jl.Items.Where(i => i.Arguments == UpdateTaskArg).ToList().ForEach(i => jl.Items.Remove(i));

            var shouldShow = state is UpdateState.UpdateAvailable or UpdateState.RestartPending;
            if (shouldShow && !removedByUser)
            {
                var item = Windows.UI.StartScreen.JumpListItem.CreateWithArguments(UpdateTaskArg, UpdateTaskLabel);
                item.GroupName = "Updates";
                item.Description = "Restart wintty to apply the pending update.";
                jl.Items.Add(item);
            }
            await jl.SaveAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] jump-list sync failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
    }
}
