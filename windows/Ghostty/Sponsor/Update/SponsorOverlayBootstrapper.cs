using System;
using System.Diagnostics;
using System.IO;
using Ghostty.Core.Config;
using Ghostty.Core.Sponsor.Update;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Top-level sponsor wiring. Called from App.OnLaunched under
/// <c>#if SPONSOR_BUILD</c>. Constructs the update pipeline manually
/// (matches the existing App.xaml.cs pattern; no DI container in the
/// shell today). Returns a handle the app can dispose on shutdown.
/// </summary>
internal sealed class SponsorOverlayBootstrapper : IDisposable
{
    private readonly UpdateService _service;
    private readonly UpdateSimulator _simulator;
    private readonly UpdatePillViewModel _pillVm;
    private readonly UpdatePopoverViewModel _popoverVm;
    private readonly TaskbarOverlayProvider _taskbar;
    private readonly UpdateToastPublisher _toast;
    private readonly UpdateJumpListProvider _jumpList;
    private readonly RestartPendingExitInterceptor _exitInterceptor;
    private readonly SponsorActivationRouter _router;

    public SponsorActivationRouter Router => _router;
    public UpdateSimulator Simulator => _simulator;

    private SponsorOverlayBootstrapper(
        UpdateService service,
        UpdateSimulator simulator,
        UpdatePillViewModel pillVm,
        UpdatePopoverViewModel popoverVm,
        TaskbarOverlayProvider taskbar,
        UpdateToastPublisher toast,
        UpdateJumpListProvider jumpList,
        RestartPendingExitInterceptor exitInterceptor,
        SponsorActivationRouter router)
    {
        _service = service;
        _simulator = simulator;
        _pillVm = pillVm;
        _popoverVm = popoverVm;
        _taskbar = taskbar;
        _toast = toast;
        _jumpList = jumpList;
        _exitInterceptor = exitInterceptor;
        _router = router;
    }

    public static SponsorOverlayBootstrapper Wire(
        Window mainWindow,
        IConfigService config,
        DispatcherQueue dispatcher,
        UpdateSimulator simulator)
    {
        var service = new UpdateService(simulator, config);

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var skipPath = Path.Combine(localAppData, "Ghostty", "update-skips.json");
        var skipList = new UpdateSkipList(skipPath);

        var pillVm = new UpdatePillViewModel(service, dispatcher, skipList);
        var popoverVm = new UpdatePopoverViewModel(service, skipList, dispatcher);

        // Inject the pill into the title bar overlay presenter.
        var host = mainWindow.Content is FrameworkElement root
            ? root.FindName("SponsorOverlayHost") as ContentPresenter
            : null;
        if (host is not null)
        {
            var pill = new UpdatePill();
            pill.Bind(pillVm, popoverVm);
            host.Content = pill;
        }
        else
        {
            Debug.WriteLine("[sponsor/update] SponsorOverlayHost not found; pill will not render");
        }

        var taskbar = new TaskbarOverlayProvider(service, dispatcher);
        taskbar.Attach(mainWindow);
        var toast = new UpdateToastPublisher(service, dispatcher);
        toast.Attach(mainWindow);
        var jumpList = new UpdateJumpListProvider(service);
        jumpList.Attach();
        var exit = new RestartPendingExitInterceptor(service, dispatcher);
        exit.Attach(mainWindow);

        var router = new SponsorActivationRouter(service, dispatcher);

        var exePath = Environment.ProcessPath ?? string.Empty;
        if (!string.IsNullOrEmpty(exePath))
        {
            WinttyProtocolRegistrar.EnsureRegistered(exePath);
        }

        service.Start();
        return new SponsorOverlayBootstrapper(
            service, simulator, pillVm, popoverVm,
            taskbar, toast, jumpList, exit, router);
    }

    public void Dispose()
    {
        _exitInterceptor.Dispose();
        _jumpList.Dispose();
        _toast.Dispose();
        _taskbar.Dispose();
        _popoverVm.Dispose();
        _pillVm.Dispose();
        _service.Dispose();
    }
}
