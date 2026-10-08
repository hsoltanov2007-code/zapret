using System.Windows;
using System.Net.Http;
using System.Security.Principal;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Zapret1;
using Northpass.Engine.Native;
using Northpass.Services;
using Northpass.Services.Installation;
using Northpass.ViewModels;
using Northpass.Presentation;

namespace Northpass;

public partial class App : Application
{
    private Mutex? _instance;
    private HttpClient? _http;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var strings = new UiStrings("ru");
        MainViewModel? model = null;
        try
        {
            var settings = new SettingsStore(SettingsStore.DefaultFolder);
            try { strings = new UiStrings(settings.Load().Language); } catch { /* The model preserves and reports unreadable settings. */ }
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            _instance = new Mutex(true, "Local\\Northpass-" + sid, out bool first);
            if (!first)
            {
                ProductDialog.Show(null, strings, strings["AlreadyOpen"], false);
                _instance.Dispose(); _instance = null;
                Shutdown(); return;
            }
            var registry = new EngineRegistry();

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
            EngineInstallationManager? native = null;
            if (NativeCatalog.IsBundled)
            {
                using var manifest = NativeCatalog.OpenTrustedManifest();
                native = new EngineInstallationManager(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-Native-0.2"),
                    _http, new WindowsInstallationSecurity(), EngineManifest.Parse(manifest),
                    offlinePayload: Path.Combine(AppContext.BaseDirectory, "engine-payload", "native-offline.zip"),
                    probe: NativeEngine.VerifyInstalledVersionAsync, requireOfflinePayload: true);
                registry.Register(NativeEngine.Metadata, () => new NativeEngine(native, useNamedPipe: e.Args.Contains("--native-ipc-check")));
            }
            // Internal acceptance path; never switches the consumer UI to a non-bypass engine.
            if (e.Args.Contains("--native-check") || e.Args.Contains("--native-ipc-check"))
            {
                if (native is null) throw new IOException("The native offline build is missing.");
                var installed = await native.EnsureInstalledAsync();
                if (await native.DetectAsync() != installed) throw new IOException("Native installation verification failed.");
                await using var controller = new EngineController(registry);
                await controller.ConnectAsync(new(installed.ExecutablePath, NativeCatalog.Idle()), autoRecover: false);
                if ((await controller.GetStatusAsync()).State != Northpass.Models.EngineState.Active) throw new IOException("Native initialization failed.");
                if (e.Args.Contains("--native-ipc-check") && await controller.GetPerformanceAsync() is not { KernelLossUnknown: true })
                    throw new IOException("Authenticated native metrics unavailable.");
                await controller.DisconnectAsync();
                Shutdown(0); return;
            }
            // Packaging acceptance probe: exercises the published application's real
            // offline composition without opening a window or intercepting traffic.
            if (e.Args.Contains("--installation-check"))
            {
                var installed = await flowseal.EnsureInstalledAsync();
                if (await flowseal.DetectAsync() != installed) throw new IOException("Installation verification failed.");
                Shutdown(0); return;
            }
            bool developerTools = false;
#if DEBUG
            developerTools = e.Args.Contains("--dev-tools");
#endif
            model = new MainViewModel(new EngineController(registry), settings, profiles,
                new UpdateChecker(_http), new DesktopServices(), Dispatcher, flowseal,
                new ServiceProbeService(new NetworkServiceProbeTransport()), dataLists, new StrategyTestRecordStore(Path.Combine(settings.Folder, "strategy-tests")), developerTools);
            var window = new MainWindow(model);
            MainWindow = window;
            if (!await StartupPresentation.ShowAsync(window, model, e.Args.Contains("--tray"))) Shutdown();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            if (model is not null)
            { try { await model.DisposeAsync(); } catch (Exception cleanup) { System.Diagnostics.Trace.WriteLine(cleanup); } }
            if (!e.Args.Contains("--installation-check") && !e.Args.Contains("--native-check") && !e.Args.Contains("--native-ipc-check"))
                ProductDialog.Show(MainWindow?.IsVisible == true ? MainWindow : null, strings, strings["StartupFailed"], false);
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
