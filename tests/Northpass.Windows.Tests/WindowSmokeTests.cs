using System.Windows.Controls;
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
                var model = new MainViewModel(new EngineController(registry), registry, new SettingsStore(temporary), profiles,
                    new DiagnosticsService(http), new UpdateChecker(http), new NoInteraction(), app.Dispatcher);
                var window = new MainWindow(model); app.MainWindow = window;
                model.InitializeAsync(null).GetAwaiter().GetResult();
                window.Show(); window.UpdateLayout();
                Assert.True(window.IsVisible);
                Assert.Equal("Disconnected", model.StatusText);
                var tabs = Assert.IsType<TabControl>(window.FindName("NavigationTabs"));
                Assert.Equal(5, tabs.Items.Count);
                for (int page = 0; page < tabs.Items.Count; page++) { tabs.SelectedIndex = page; window.UpdateLayout(); }
                model.Language = "ru"; Assert.Equal("Главная", model.Strings["Dashboard"]);
                model.Language = "az"; Assert.Equal("İdarə paneli", model.Strings["Dashboard"]);
                window.Close();
                Assert.False(window.IsVisible);
                complete.TrySetResult();
            }
            catch (Exception ex) { complete.TrySetException(ex); }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    private sealed class NoInteraction : IDesktopServices
    {
        public string? PickEngine(EngineDescriptor descriptor) => throw new InvalidOperationException("Unexpected dialog.");
        public string? PickProfile() => throw new InvalidOperationException("Unexpected dialog.");
        public string? PickExport(string name, string extension) => throw new InvalidOperationException("Unexpected dialog.");
        public bool ConfirmTrust(string message) => throw new InvalidOperationException("Unexpected engine launch.");
        public StrategyProfile? EditProfile(ProfileStore store, StrategyProfile? original) => throw new InvalidOperationException("Unexpected dialog.");
        public Task SetAutoStartAsync(bool enabled) => throw new InvalidOperationException("Unexpected autostart change.");
    }
}
