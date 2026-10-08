using System.Windows.Controls;
using System.Windows.Threading;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Zapret2;
using Northpass.Models;
using Northpass.Services;
using Northpass.ViewModels;

namespace Northpass.Windows.Tests;

public sealed class WindowSmokeTests
{
    // Real WPF window on a Windows STA thread. No driver or engine is started.
    [Fact]
    public async Task WindowOpensAllPagesAndClosesWithoutStartingAnEngine()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string temporary = Path.Combine(Path.GetTempPath(), "northpass-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var app = new App(); app.InitializeComponent();
                var registry = new EngineRegistry(); registry.Register(Zapret2Engine.Metadata, () => new Zapret2Engine());
                var profiles = new ProfileStore(Path.Combine(temporary, "profiles"));
                profiles.Save(new() { Id = "draft", Name = "Draft", Arguments = [] });
                using var http = new HttpClient();
                var desktop = new SetupConsent(); var installation = new FakeInstallation();
                var store = new SettingsStore(temporary);
                var model = new MainViewModel(new EngineController(registry), registry, store, profiles,
                    new DiagnosticsService(http), new UpdateChecker(http), desktop, app.Dispatcher, installation);
                var window = new MainWindow(model); app.MainWindow = window;
                model.InitializeAsync(null).GetAwaiter().GetResult();
                window.Show(); window.UpdateLayout();
                Assert.True(window.IsVisible);
                Assert.Equal("Disconnected", model.StatusText);
                Assert.Equal("Engine ready", model.EngineSetupText);
                Assert.Equal(1, desktop.Consents); Assert.Equal(1, installation.Setups);
                Assert.True(store.Load().EngineSetupConsent);
                Assert.False(model.CanChooseEngine);
                var tabs = Assert.IsType<TabControl>(window.FindName("NavigationTabs"));
                Assert.Equal(5, tabs.Items.Count);
                for (int page = 0; page < tabs.Items.Count; page++) { tabs.SelectedIndex = page; window.UpdateLayout(); }
                model.Language = "ru"; Assert.Equal("Главная", model.Strings["Dashboard"]);
                model.Language = "az"; Assert.Equal("İdarə paneli", model.Strings["Dashboard"]);
                var frame = new DispatcherFrame();
                window.Closed += (_, _) => frame.Continue = false;
                window.Close();
                Dispatcher.PushFrame(frame);
                Assert.False(window.IsVisible);
                // Next launch reuses the installation without asking for consent or installing again.
                var next = new MainViewModel(new EngineController(registry), registry, store, profiles,
                    new DiagnosticsService(http), new UpdateChecker(http), desktop, app.Dispatcher, installation);
                next.InitializeAsync(null).GetAwaiter().GetResult();
                Assert.Equal(1, desktop.Consents); Assert.Equal(1, installation.Setups);
                Assert.Equal("Engine ready", next.EngineSetupText);
                next.DisposeAsync().AsTask().GetAwaiter().GetResult();
                complete.TrySetResult();
            }
            catch (Exception ex) { complete.TrySetException(ex); }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    private sealed class SetupConsent : IDesktopServices
    {
        public int Consents;
        public string? PickEngine(EngineDescriptor descriptor) => throw new InvalidOperationException("Unexpected dialog.");
        public string? PickProfile() => throw new InvalidOperationException("Unexpected dialog.");
        public string? PickExport(string name, string extension) => throw new InvalidOperationException("Unexpected dialog.");
        public bool ConfirmTrust(string message) { Assert.Contains("Install the reviewed official Zapret2", message); Consents++; return true; }
        public StrategyProfile? EditProfile(ProfileStore store, StrategyProfile? original) => throw new InvalidOperationException("Unexpected dialog.");
        public Task SetAutoStartAsync(bool enabled) => throw new InvalidOperationException("Unexpected autostart change.");
    }
    // UI flow fixture only. Real executable/ACL checks are in EngineInstallationWindowsTests.
    private sealed class FakeInstallation : IEngineInstallationManager
    {
        public int Setups;
        private InstalledEngine? _engine;
        public string EngineId => "zapret2";
        public Task<InstalledEngine?> DetectAsync(CancellationToken token = default) => Task.FromResult(_engine);
        public Task<InstalledEngine> EnsureInstalledAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default)
        {
            Setups++; _engine = new("zapret2", new string('a', 40), "test", "C:\\protected\\winws2.exe");
            progress?.Report(new("Ready", 1, 1)); return Task.FromResult(_engine);
        }
        public Task<EngineUpdateStatus> CheckForUpdatesAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstalledEngine> UpdateAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstalledEngine> RollbackAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<IAsyncDisposable> AcquireLaunchLeaseAsync(string path, CancellationToken token = default) => throw new InvalidOperationException("UI smoke test must not launch an engine.");
    }
}
