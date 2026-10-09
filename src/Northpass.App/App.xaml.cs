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
using Northpass.Broker;

namespace Northpass;

public partial class App : Application
{
    private Mutex? _instance;
    private HttpClient? _http;
    private BrokerClient? _broker;
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
            IEngineInstallationManager flowseal;
            IEngineInstallationManager? native = null;
            int handoff=e.Args.ToList().IndexOf("--broker-test-handoff");
            _broker=new BrokerClient(handoff>=0 && handoff+1<e.Args.Length?e.Args[handoff+1]:null);
            flowseal=new BrokerInstallation(_broker,"zapret1");
            registry.Register(Zapret1Engine.Metadata,()=>new BrokerEngine(_broker,"zapret1",dataLists));
            if(NativeCatalog.IsBundled){native=new BrokerInstallation(_broker,"native");registry.Register(NativeEngine.Metadata,()=>new BrokerEngine(_broker,"native"));}
            if(e.Args.Contains("--broker-check"))
            {
                if(native is null)throw new IOException("Native offline components missing.");
                await BrokerAcceptance.CheckAsync(_broker,flowseal,native,e.Args);
                await _broker.DisposeAsync();_broker=null;Shutdown(0);return;
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
            var desktop = new DesktopServices();
            // Preserve a saved startup preference while migrating the old elevated
            // task to this user's unelevated Run entry. Headless checks skip this.
            try { if(settings.Load().StartWithWindows && Path.GetFileName(Environment.ProcessPath)=="Northpass.exe")await desktop.SetAutoStartAsync(true); }
            catch(Exception migration){System.Diagnostics.Trace.WriteLine("Startup preference migration: "+migration.Message);}
            model = new MainViewModel(new EngineController(registry), settings, profiles,
                new UpdateChecker(_http), desktop, Dispatcher, flowseal,
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
            if(_broker is not null){try{await _broker.DisposeAsync();}catch(Exception cleanup){System.Diagnostics.Trace.WriteLine(cleanup);}_broker=null;}
            if(e.Args.Contains("--broker-check"))
            {
                int output=e.Args.ToList().IndexOf("--evidence");
                if(output>=0 && output+1<e.Args.Length)File.WriteAllText(e.Args[output+1],ex.ToString());
            }
            if (!e.Args.Contains("--installation-check") && !e.Args.Contains("--native-check") && !e.Args.Contains("--native-ipc-check") && !e.Args.Contains("--broker-check"))
                ProductDialog.Show(MainWindow?.IsVisible == true ? MainWindow : null, strings, strings["StartupFailed"], false);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        // The window has already awaited owned engine shutdown. Closing the
        // channel tells the helper to clean up any remaining owned operations.
        if (_broker is not null) Task.Run(async () => await _broker.DisposeAsync()).GetAwaiter().GetResult();_broker=null;
        _http?.Dispose();
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
