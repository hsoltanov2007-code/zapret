using System.Windows;
using System.Net.Http;
using System.Security.Principal;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Zapret1;
using Northpass.Services;
using Northpass.Services.Installation;
using Northpass.ViewModels;

namespace Northpass;

public partial class App : Application
{
    private Mutex? _instance;
    private HttpClient? _http;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            _instance = new Mutex(true, "Local\\Northpass-" + sid, out bool first);
            if (!first)
            {
                ProductDialog.Show(null, new UiStrings("en"), new UiStrings("en")["AlreadyOpen"], false);
                _instance.Dispose(); _instance = null;
                Shutdown(); return;
            }
            var registry = new EngineRegistry();

            var settings = new SettingsStore(SettingsStore.DefaultFolder);
            var profiles = new ProfileStore(System.IO.Path.Combine(settings.Folder, "profiles"));
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
            var dataLists = new DataListStore(Path.Combine(settings.Folder, "lists"));
            var data = new ProtectedEngineDataProvider(dataLists,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-StrategyData"), new WindowsInstallationSecurity());
            using var flowsealManifest = FlowsealCatalog.OpenTrustedManifest();
            var flowsealPrevious = new List<EngineManifest>();
            foreach (var stream in FlowsealCatalog.OpenPreviousTrustedManifests()) using (stream) flowsealPrevious.Add(EngineManifest.Parse(stream));
            var flowseal = new EngineInstallationManager(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-Flowseal"),
                _http, new WindowsInstallationSecurity(), EngineManifest.Parse(flowsealManifest), previous: flowsealPrevious,
                offlinePayload: Path.Combine(AppContext.BaseDirectory, "engine-payload", "flowseal-offline.zip"),
                probe: Zapret1Engine.VerifyInstalledVersionAsync, requireOfflinePayload: true);
            registry.Register(Zapret1Engine.Metadata, () => new Zapret1Engine(flowseal, data));
            // Packaging acceptance probe: exercises the published application's real
            // offline composition without opening a window or intercepting traffic.
            if (e.Args.Contains("--installation-check"))
            {
                var installed = await flowseal.EnsureInstalledAsync();
                if (await flowseal.DetectAsync() != installed) throw new IOException("Installation verification failed.");
                Shutdown(0); return;
            }
            var model = new MainViewModel(new EngineController(registry), settings, profiles,
                new UpdateChecker(_http), new DesktopServices(), Dispatcher, flowseal,
                new ServiceProbeService(new NetworkServiceProbeTransport()), dataLists, new StrategyTestRecordStore(Path.Combine(settings.Folder, "strategy-tests")));
            var window = new MainWindow(model);
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--tray")) { window.WindowState = WindowState.Minimized; window.Hide(); }
            await model.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            if (!e.Args.Contains("--installation-check"))
                ProductDialog.Show(MainWindow, new UiStrings("en"), new UiStrings("en")["StartupFailed"], false);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _http?.Dispose();
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
