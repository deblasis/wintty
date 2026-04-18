using System;
using System.Diagnostics;
using System.IO;
using Ghostty.Core.Config;
using Ghostty.Core.Sponsor.Update;
using Microsoft.Extensions.Logging;
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
    // Owned lifetime resources that UpdateService deliberately doesn't
    // take ownership of. Disposed in reverse construction order.
    private readonly Ghostty.Core.Sponsor.Update.VelopackUpdateDriver _driver;
    private readonly System.Net.Http.HttpClient _http;

    public SponsorActivationRouter Router => _router;
    public UpdateSimulator Simulator => _simulator;
    public UpdateService Service => _service;

    private SponsorOverlayBootstrapper(
        UpdateService service,
        UpdateSimulator simulator,
        UpdatePillViewModel pillVm,
        UpdatePopoverViewModel popoverVm,
        TaskbarOverlayProvider taskbar,
        UpdateToastPublisher toast,
        UpdateJumpListProvider jumpList,
        RestartPendingExitInterceptor exitInterceptor,
        SponsorActivationRouter router,
        Ghostty.Core.Sponsor.Update.VelopackUpdateDriver driver,
        System.Net.Http.HttpClient http)
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
        _driver = driver;
        _http = http;
    }

    public static SponsorOverlayBootstrapper Wire(
        Window mainWindow,
        IConfigService config,
        DispatcherQueue dispatcher,
        UpdateSimulator simulator,
        Ghostty.Core.Sponsor.Auth.ISponsorTokenProvider tokens,
        ILoggerFactory? loggerFactory = null)
    {
        var http = new System.Net.Http.HttpClient(
            new System.Net.Http.HttpClientHandler
            {
                // Bearer must be stripped on the R2 hop (presigned URL
                // carries its own signature, and R2 rejects extra Authorization
                // headers). WinttyManifestClient follows the 302 manually.
                AllowAutoRedirect = false,
            })
        {
            Timeout = System.TimeSpan.FromSeconds(30),
        };
        var source = new WinttyUpdateSource(
            http, tokens,
            channel: "stable",
            apiBase: new System.Uri("https://api.wintty.io"));
        var adapter = new VelopackManagerAdapter(source);  // Velopack 0.0.1298 ctor has no logger param
        var driverLogger = loggerFactory?.CreateLogger<Ghostty.Core.Sponsor.Update.VelopackUpdateDriver>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Ghostty.Core.Sponsor.Update.VelopackUpdateDriver>.Instance;
        var driver = new Ghostty.Core.Sponsor.Update.VelopackUpdateDriver(adapter, tokens, driverLogger);

#if DEBUG
        // DEBUG builds keep the simulator as a secondary driver so the
        // palette "Simulate: *" entries still emit into UpdateService.
        var service = new UpdateService(driver, config, secondary: simulator);
#else
        var service = new UpdateService(driver, config);
#endif

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
            taskbar, toast, jumpList, exit, router, driver, http);
    }

    public void Dispose()
    {
        _exitInterceptor.Dispose();
        _jumpList.Dispose();
        _toast.Dispose();
        _taskbar.Dispose();
        _popoverVm.Dispose();
        _pillVm.Dispose();
        // Service first - stops the poll loop and unsubscribes StateChanged
        // - before the driver releases its semaphore and token subscription.
        _service.Dispose();
        _driver.Dispose();
        // HttpClient last: the driver may still have an in-flight request
        // when UpdateService.Dispose() returns; disposing the client before
        // the driver would crash those requests.
        _http.Dispose();
    }
}
