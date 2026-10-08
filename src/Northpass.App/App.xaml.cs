using System.Windows;
using System.Net.Http;
using System.Security.Principal;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Zapret2;
using Northpass.Services;
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
                MessageBox.Show("Northpass is already open. Use its system tray icon.", "Northpass");
                _instance.Dispose(); _instance = null;
                Shutdown(); return;
            }
            var registry = new EngineRegistry();
            registry.Register(Zapret2Engine.Metadata, () => new Zapret2Engine());
            var settings = new SettingsStore(SettingsStore.DefaultFolder);
            var profiles = new ProfileStore(System.IO.Path.Combine(settings.Folder, "profiles"));
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            var model = new MainViewModel(new EngineController(registry), registry, settings, profiles,
                new DiagnosticsService(_http), new UpdateChecker(_http), new DesktopServices(), Dispatcher);
            var window = new MainWindow(model);
            MainWindow = window;
            window.Show();
            if (e.Args.Contains("--tray")) { window.WindowState = WindowState.Minimized; window.Hide(); }
            await model.InitializeAsync(Zapret2Engine.Discover(AppContext.BaseDirectory));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Northpass could not initialize: " + ex.Message, "Northpass", MessageBoxButton.OK, MessageBoxImage.Error);
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
