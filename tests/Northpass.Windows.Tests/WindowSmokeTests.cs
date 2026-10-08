using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        string stage = "STA startup";
        var thread = new Thread(() =>
        {
            string temporary = Path.Combine(Path.GetTempPath(), "northpass-ui-" + Guid.NewGuid().ToString("N"));
            Exception? failure = null;
            try
            {
                var app = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                // A continuously running dispatcher allows genuine asynchronous UI setup/cleanup.
                app.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    MainWindow? window = null;
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        var registry = new EngineRegistry(); registry.Register(Zapret2Engine.Metadata, () => new Zapret2Engine());
                        var profiles = new ProfileStore(Path.Combine(temporary, "profiles"));
                        profiles.Save(new() { Id = "draft", Name = "Draft", Engine = "ZAPRET2", Arguments = [] });
                        using var http = new HttpClient();
                        var desktop = new SetupConsent(); var installation = new FakeInstallation();
                        var store = new SettingsStore(temporary);
                        var model = new MainViewModel(new EngineController(registry), registry, store, profiles,
                            new DiagnosticsService(http), new UpdateChecker(http), desktop, app.Dispatcher, installation);
                        window = new MainWindow(model); app.MainWindow = window;
                        window.Closed += (_, _) => closed.TrySetResult();
                        stage = "automatic bundled setup";
                        await model.InitializeAsync(null);
                        stage = "window layout";
                        window.Show(); window.UpdateLayout();
                        Assert.True(window.IsVisible);
                        Assert.Equal("Ready", model.StatusText);
                        Assert.Equal("Ready", model.EngineSetupText);
                        Assert.Equal(0, desktop.Consents); Assert.Equal(1, installation.Setups);
                        Assert.False(store.Load().EngineSetupConsent); Assert.False(model.CanChooseEngine);
                        Assert.True(model.ManagedEngine);
                        var tabs = Assert.IsType<TabControl>(window.FindName("NavigationTabs"));
                        Assert.Equal(5, tabs.Items.Count);
                        Assert.False(model.ShowAdvancedTools);
                        Assert.Equal(Visibility.Collapsed, ((TabItem)tabs.Items[1]).Visibility);
                        foreach (string language in model.Languages)
                        {
                            model.Language = language; tabs.SelectedIndex = 0; window.UpdateLayout();
                            AssertConsumerText(window);
                            Screenshot(window, language);
                            tabs.SelectedIndex = 3; window.UpdateLayout(); AssertConsumerText(window);
                        }
                        model.Language = "en";
                        model.Fail(new IOException("SHA-256 mismatch: Zapret2 WinDivert64.sys at C:\\protected"));
                        tabs.SelectedIndex = 0; window.UpdateLayout(); AssertConsumerText(window);
                        Assert.Contains("SHA-256", model.ResultText);
                        Assert.DoesNotContain("SHA-256", model.UserMessage);
                        model.ShowAdvancedTools = true;
                        Assert.Equal(Visibility.Visible, ((TabItem)tabs.Items[1]).Visibility);
                        for (int page = 0; page < tabs.Items.Count; page++) { tabs.SelectedIndex = page; window.UpdateLayout(); }
                        model.Language = "ru"; Assert.Equal("Главная", model.Strings["Dashboard"]);
                        Assert.Equal("Готово", model.EngineSetupText);
                        model.Language = "az"; Assert.Equal("Əsas", model.Strings["Dashboard"]);
                        Assert.Equal("Hazır", model.EngineSetupText);
                        stage = "custom modal accept and cancel";
                        foreach (bool accept in new[] { false, true })
                        {
                            var dialog = new ProductDialog(model.Strings, model.Strings["EngineRepairConsent"]) { Owner = window };
                            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                var button = Assert.IsType<Button>(dialog.FindName(accept ? "AcceptButton" : "CancelButton"));
                                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }));
                            Assert.Equal(accept, dialog.ShowDialog());
                        }
                        stage = "license viewer";
                        var licenses = new LicensesWindow(model.Strings) { Owner = window };
                        licenses.Loaded += (_, _) => licenses.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Assert.Contains("Zapret2", ((TextBox)licenses.FindName("LicenseText")).Text);
                            licenses.Close();
                        }));
                        licenses.ShowDialog();
                        stage = "asynchronous close";
                        window.Close(); await closed.Task;
                        Assert.False(window.IsVisible);
                        stage = "subsequent launch reuse";
                        var next = new MainViewModel(new EngineController(registry), registry, store, profiles,
                            new DiagnosticsService(http), new UpdateChecker(http), desktop, app.Dispatcher, installation);
                        await next.InitializeAsync(null);
                        Assert.Equal(0, desktop.Consents); Assert.Equal(1, installation.Setups);
                        Assert.Equal("Ready", next.EngineSetupText);
                        await next.DisposeAsync();
                    }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        if (window is not null && !closed.Task.IsCompleted) { window.Close(); await closed.Task; }
                        stage = "dispatcher shutdown";
                        app.Shutdown();
                        // This test starts Dispatcher.Run directly; Application.Run does not own it.
                        app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }));
                Dispatcher.Run();
                if (failure is null) complete.TrySetResult(); else complete.TrySetException(failure);
            }
            catch (Exception ex) { complete.TrySetException(ex); }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await complete.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
        catch (TimeoutException) { throw new TimeoutException("WPF smoke timed out at: " + stage); }
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void AssertConsumerText(Window window)
    {
        string text = string.Join("\n", Visuals(window).OfType<TextBlock>().Where(block => block.IsVisible).Select(block => block.Text));
        foreach (string forbidden in new[] { "Zapret", "WinDivert", "SHA-256", "GitHub", "winws", "revision", "Program Files", "JSON" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }
    private static void Screenshot(Window window, string language)
    {
        string output = Path.Combine(FindRepository(), "TestResults"); Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, "northpass-v0.4-" + language + ".png")); png.Save(file);
    }
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Northpass.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository not found.");
    }
    private sealed class SetupConsent : IDesktopServices
    {
        public int Consents;
        public string? PickEngine(EngineDescriptor descriptor) => throw new InvalidOperationException("Unexpected dialog.");
        public string? PickProfile() => throw new InvalidOperationException("Unexpected dialog.");
        public string? PickExport(string name, string extension) => throw new InvalidOperationException("Unexpected dialog.");
        public bool ConfirmTrust(string message) { Consents++; throw new InvalidOperationException("Automatic preparation must not prompt for component consent."); }
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
        public Task<InstalledEngine> RepairAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstalledEngine> RollbackAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<IAsyncDisposable> AcquireLaunchLeaseAsync(string path, CancellationToken token = default) => throw new InvalidOperationException("UI smoke test must not launch an engine.");
    }
}
