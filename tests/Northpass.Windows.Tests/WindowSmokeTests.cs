using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Zapret1;
using Northpass.Models;
using Northpass.Services;
using Northpass.ViewModels;
using Northpass.Presentation;
using ValidationResult = Northpass.Models.ValidationResult;

namespace Northpass.Windows.Tests;

public sealed class WindowSmokeTests
{
    // Real WPF window on a Windows STA thread. Session state uses an explicit fake;
    // real PE/driver/ownership checks live in FlowsealWindowsTests.
    [Fact]
    public async Task ConsumerWindowPreparesAutomaticallyAndKeepsAccessStatusHonest()
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
                        var registry = new EngineRegistry(); registry.Register(Zapret1Engine.Metadata, () => new SessionFixture());
                        var profiles = new ProfileStore(Path.Combine(temporary, "profiles"));
                        profiles.Save(new() { Id = "draft", Name = "Draft", Engine = "ZAPRET2", Arguments = [] });
                        using var http = new HttpClient();
                        var desktop = new SetupConsent(); var installation = new FakeInstallation();
                        var store = new SettingsStore(temporary);
                        store.Save(new() { SelectedProfileId = "draft", ShowAdvancedTools = true });
                        var model = new MainViewModel(new EngineController(registry), store, profiles,
                            new UpdateChecker(http), desktop, app.Dispatcher, installation, new ServiceProbeService(new ProbeFixture()));
                        window = new MainWindow(model); app.MainWindow = window;
                        Motion.SetReduceMotion(window, true);
                        window.Closed += (_, _) => closed.TrySetResult();
                        stage = "automatic bundled setup";
                        var startup = new SplashWindow(model.Strings);
                        Motion.SetReduceMotion(startup, true);
                        startup.Loaded += (_, _) =>
                        {
                            Assert.False(window.IsVisible);
                            Assert.Equal("Запускаем Northpass", startup.Status);
                            startup.UpdateLayout(); Screenshot(startup, "ru-splash");
                        };
                        Assert.True(await StartupPresentation.ShowAsync(window, model, splash: startup));
                        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        Assert.False(startup.IsVisible);
                        stage = "window layout";
                        window.UpdateLayout();
                        Assert.True(window.IsVisible);
                        Assert.Equal("Готово", model.StatusText);
                        Assert.Equal("Готово", model.EngineSetupText);
                        Assert.Equal(0, desktop.Consents); Assert.Equal(1, installation.Setups);
                        Assert.False(store.Load().EngineSetupConsent);
                        Assert.Equal("flowseal-general", model.SelectedProfile?.Id);
                        Assert.Empty(profiles.Load().Profiles.Single(profile => profile.Id == "draft").Arguments);
                        Assert.All(model.Profiles, profile => Assert.Equal("zapret1", profile.Engine));
                        var selector = Assert.IsType<ComboBox>(window.FindName("HomeStrategySelector"));
                        Assert.True(selector.IsVisible); Assert.True(selector.IsEnabled);
                        Assert.Equal(5, selector.Items.Count);
                        Assert.Contains(Visuals(selector).OfType<TextBlock>().Where(block => block.IsVisible), block => block.Text == model.SelectedStrategy!.Label);
                        Assert.Equal(3, model.ServiceCards.Count);
                        Assert.Equal(new[] { "youtube", "discord", "telegram" }, model.ServiceCards.Select(card => card.Id));
                        Assert.Equal(ServiceAvailability.Available, model.ServiceCards[0].Availability);
                        Assert.Equal(ServiceAvailability.Limited, model.ServiceCards[1].Availability);
                        Assert.Equal(ServiceAvailability.Unavailable, model.ServiceCards[2].Availability);
                        Assert.DoesNotContain(model.Strategies, strategy => strategy.Label.Contains("Flowseal"));
                        selector.SelectedItem = model.Strategies.Single(choice => choice.Profile.StrategyId == "alt");
                        Assert.Equal("flowseal-alt", store.Load().SelectedProfileId);
                        Assert.Equal(model.Strings["Strategy_alt"], model.SelectedStrategy!.Label);
                        var tabs = Assert.IsType<TabControl>(window.FindName("NavigationTabs"));
                        Assert.Equal(5, tabs.Items.Count);
                        Assert.False(model.ShowAdvancedTools);
                        Assert.Equal(Visibility.Collapsed, ((TabItem)tabs.Items[1]).Visibility);
                        foreach (string language in model.Languages)
                        {
                            model.Language = language; tabs.SelectedIndex = 0; window.UpdateLayout();
                            AssertConsumerText(window);
                            Assert.Contains(Visuals(selector).OfType<TextBlock>().Where(block => block.IsVisible), block => block.Text == model.SelectedStrategy!.Label);
                            Screenshot(window, language);
                            tabs.SelectedIndex = 3; window.UpdateLayout(); AssertConsumerText(window);
                            var languageSelector = Assert.IsType<ComboBox>(window.FindName("LanguageSelector"));
                            Assert.Equal(language, languageSelector.SelectedValue);
                            Assert.Contains(Visuals(languageSelector).OfType<TextBlock>().Where(block => block.IsVisible), block => block.Text == LanguageChoice.All.Single(choice => choice.Code == language).Label);
                            Assert.Contains(Visuals(languageSelector).OfType<Image>(), image => image.Source is DrawingImage);
                            Assert.Equal(language, store.Load().Language);
                            Assert.DoesNotContain(Visuals(window).OfType<CheckBox>(), checkbox => (checkbox.Content?.ToString() ?? "").Contains("дополнительные"));
                            Screenshot(window, language + "-settings");
                            tabs.SelectedIndex = 2; window.UpdateLayout(); AssertConsumerText(window);
                            tabs.SelectedIndex = 4; window.UpdateLayout(); AssertConsumerText(window);
                            Screenshot(window, language + "-about");
                        }
                        tabs.SelectedIndex = 3; window.UpdateLayout();
                        var settingsLanguage = (ComboBox)window.FindName("LanguageSelector");
                        settingsLanguage.SelectedValue = "az";
                        Assert.Equal("az", model.Language); Assert.Equal("az", store.Load().Language);
                        settingsLanguage.SelectedValue = "en";
                        Assert.Equal("en", model.Language); Assert.Equal("en", store.Load().Language);
                        model.Fail(new IOException("SHA-256 mismatch: Zapret2 WinDivert64.sys at C:\\protected"));
                        tabs.SelectedIndex = 0; window.UpdateLayout(); AssertConsumerText(window);
                        Assert.Contains("SHA-256", model.ResultText);
                        Assert.DoesNotContain("SHA-256", model.UserMessage);
                        model.SaveSettingsCommand.Execute(null);
                        Assert.True(store.Load().ShowAdvancedTools); // Preserve the legacy field, but ignore it in the product.
                        Assert.False(model.ShowAdvancedTools);
                        model.PageIndex = 1; Assert.Equal(0, model.PageIndex);
                        Assert.Equal(Visibility.Collapsed, ((TabItem)tabs.Items[1]).Visibility);
                        model.Language = "ru"; Assert.Equal("Главная", model.Strings["Dashboard"]);
                        Assert.Equal("Готово", model.EngineSetupText);
                        model.Language = "az"; Assert.Equal("Əsas", model.Strings["Dashboard"]);
                        Assert.Equal("Hazır", model.EngineSetupText);
                        stage = "consumer connect/disconnect with a session fixture";
                        model.Language = "en"; tabs.SelectedIndex = 0;
                        model.ConnectCommand.Execute(null);
                        await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        Assert.True(model.SessionOpen);
                        Assert.Equal("Connection started", model.StatusText);
                        Assert.Contains("checked separately", model.ConnectionNote);
                        window.UpdateLayout(); AssertConsumerText(window);
                        Assert.False(selector.IsEnabled);
                        model.ConnectCommand.Execute(null);
                        await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        Assert.False(model.SessionOpen); Assert.Equal("Disconnected", model.StatusText);
                        Assert.Equal(0, desktop.Consents); Assert.True(selector.IsEnabled);
                        Assert.Equal(ServiceAvailability.Available, model.ServiceCards[0].Availability);
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
                            Assert.Contains("Flowseal", ((TextBox)licenses.FindName("LicenseText")).Text);
                            licenses.Close();
                        }));
                        licenses.ShowDialog();
                        stage = "asynchronous close";
                        window.Close(); await closed.Task;
                        Assert.False(window.IsVisible);
                        stage = "subsequent launch reuse";
                        var next = new MainViewModel(new EngineController(registry), store, profiles,
                            new UpdateChecker(http), desktop, app.Dispatcher, installation, new ServiceProbeService(new ProbeFixture()));
                        await next.InitializeAsync();
                        Assert.Equal(0, desktop.Consents); Assert.Equal(1, installation.Setups);
                        Assert.Equal("Ready", next.EngineSetupText);
                        Assert.Equal("en", next.Language);
                        Assert.False(next.ShowAdvancedTools);
                        await next.DisposeAsync();
                        stage = "v0.6 legacy selection migration and preference preservation";
                        store.Save(new() { SelectedProfileId = "draft", Language = "az", AutoRecover = true, MinimizeToTray = false });
                        var upgrade = new MainViewModel(new EngineController(registry), store, profiles,
                            new UpdateChecker(http), desktop, app.Dispatcher, installation, new ServiceProbeService(new ProbeFixture()));
                        await upgrade.InitializeAsync();
                        Assert.Equal("flowseal-general", upgrade.SelectedProfile?.Id);
                        Assert.Equal("az", upgrade.Language); Assert.True(upgrade.AutoRecover); Assert.False(upgrade.MinimizeToTray);
                        Assert.Equal(1, installation.Setups); await upgrade.DisposeAsync();
                        stage = "valid existing Flowseal selection";
                        store.Save(new() { SelectedProfileId = "flowseal-alt2" });
                        var retained = new MainViewModel(new EngineController(registry), store, profiles,
                            new UpdateChecker(http), desktop, app.Dispatcher, installation, new ServiceProbeService(new ProbeFixture()));
                        await retained.InitializeAsync(); Assert.Equal("flowseal-alt2", retained.SelectedProfile?.Id);
                        await retained.DisposeAsync();
                        stage = "debug-only developer surface";
                        var developer = new MainViewModel(new EngineController(registry), store, profiles,
                            new UpdateChecker(http), desktop, app.Dispatcher, installation, new ServiceProbeService(new ProbeFixture()), developerTools: true);
                        await developer.InitializeAsync(); Assert.True(developer.ShowAdvancedTools);
                        developer.PageIndex = 1; Assert.Equal(1, developer.PageIndex); await developer.DisposeAsync();
                        stage = "vector flags at multiple pixel densities";
                        VerifyVectorFlags(app);
                        stage = "splash localization and reduced motion";
                        foreach (string language in model.Languages)
                        {
                            var localizedSplash = new SplashWindow(new UiStrings(language));
                            Motion.SetReduceMotion(localizedSplash, true); localizedSplash.Show(); localizedSplash.UpdateLayout();
                            Assert.Equal(new UiStrings(language)["Initializing"], localizedSplash.Status);
                            var indicator = Assert.IsType<Grid>(localizedSplash.FindName("LoadingIndicator"));
                            Assert.False(((RotateTransform)indicator.RenderTransform).HasAnimatedProperties);
                            Screenshot(localizedSplash, language + "-splash");
                            Motion.SetReduceMotion(localizedSplash, false);
                            if (Motion.Allowed(localizedSplash)) Assert.True(((RotateTransform)indicator.RenderTransform).HasAnimatedProperties);
                            Motion.SetReduceMotion(localizedSplash, true);
                            Assert.False(((RotateTransform)indicator.RenderTransform).HasAnimatedProperties);
                            await localizedSplash.FinishAsync();
                            Assert.False(((RotateTransform)indicator.RenderTransform).HasAnimatedProperties);
                        }
                        stage = "startup cancellation owns cleanup";
                        var cancelledModel = new MainViewModel(new EngineController(registry), store, profiles,
                            new UpdateChecker(http), desktop, app.Dispatcher, new FakeInstallation(waitForCancellation: true), new ServiceProbeService(new ProbeFixture()));
                        var cancelledWindow = new MainWindow(cancelledModel); Motion.SetReduceMotion(cancelledWindow, true);
                        var cancelledSplash = new SplashWindow(cancelledModel.Strings); Motion.SetReduceMotion(cancelledSplash, true);
                        cancelledSplash.Loaded += (_, _) => app.Dispatcher.BeginInvoke(new Action(cancelledSplash.Close), DispatcherPriority.Background);
                        Assert.False(await StartupPresentation.ShowAsync(cancelledWindow, cancelledModel, splash: cancelledSplash));
                        Assert.False(cancelledWindow.IsVisible); Assert.False(cancelledSplash.IsVisible);
                        stage = "stale diagnostic cancellation and shutdown";
                        var delayed = new DelayedProbeFixture();
                        var cancellationModel = new MainViewModel(new EngineController(registry), store, profiles,
                            new UpdateChecker(http), desktop, app.Dispatcher, installation, new ServiceProbeService(delayed));
                        await cancellationModel.InitializeAsync(); Assert.True(cancellationModel.DiagnosticsRunning);
                        cancellationModel.SelectedStrategy = cancellationModel.Strategies.Single(choice => choice.Profile.StrategyId == "general");
                        await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        Assert.All(cancellationModel.ServiceCards, card => Assert.Equal(ServiceAvailability.Unknown, card.Availability));
                        await cancellationModel.DisposeAsync(); Assert.False(cancellationModel.DiagnosticsRunning);
                        Assert.True(delayed.Cancellations > 0);

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
        foreach (string forbidden in new[] { "Zapret", "WinDivert", "SHA-256", "GitHub", "winws", "revision", "Program Files", "JSON", "Northpass.ViewModels", "Show advanced tools", "Показывать дополнительные инструменты", "Əlavə alətləri göstər" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }
    private static void Screenshot(Window window, string language)
    {
        string output = Path.Combine(FindRepository(), "TestResults"); Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, "northpass-v0.7-" + language + ".png")); png.Save(file);
        if (language is "ru" or "ru-about" or "ru-settings" or "ru-splash")
        {
            // Small CI annotation preview complements the full-resolution artifact,
            // allowing visual review even where artifact-storage hosts are blocked.
            string encodedPreview = "";
            for (int width = 560; width >= 320; width -= 40)
            {
                int height = (int)(window.ActualHeight * width / window.ActualWidth);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen()) drawing.DrawImage(bitmap, new Rect(0, 0, width, height));
                var preview = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); preview.Render(visual);
                var encoded = new PngBitmapEncoder(); encoded.Frames.Add(BitmapFrame.Create(preview));
                using var bytes = new MemoryStream(); encoded.Save(bytes);
                encodedPreview = Convert.ToBase64String(bytes.ToArray());
                if (encodedPreview.Length <= 40000) break;
            }
            Assert.True(encodedPreview.Length <= 40000, "Review preview must fit GitHub's annotation bound.");
            File.WriteAllText(Path.Combine(output, language switch { "ru" => "ui-preview.txt", "ru-about" => "about-preview.txt", "ru-settings" => "settings-preview.txt", _ => "splash-preview.txt" }), encodedPreview);
        }
    }
    private static void VerifyVectorFlags(Application app)
    {
        foreach (string key in new[] { "FlagRu", "FlagEn", "FlagAz" })
            Assert.IsType<DrawingImage>(app.FindResource(key));
        var flag = (DrawingImage)app.FindResource("FlagRu");
        foreach (double scale in new[] { 1.0, 1.5, 2.0 })
        {
            int width = (int)(30 * scale), height = (int)(18 * scale);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawImage(flag, new Rect(0, 0, 30, 18));
            var pixels = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); pixels.Render(visual);
            var bytes = new byte[width * height * 4]; pixels.CopyPixels(bytes, width * 4, 0);
            var expected = new[] { Color.FromRgb(245, 245, 245), Color.FromRgb(49, 90, 168), Color.FromRgb(201, 71, 79) };
            for (int stripe = 0; stripe < 3; stripe++)
            {
                int offset = ((int)((3 + stripe * 6) * scale) * width + (int)(15 * scale)) * 4;
                Assert.Equal(expected[stripe].B, bytes[offset]); Assert.Equal(expected[stripe].G, bytes[offset + 1]);
                Assert.Equal(expected[stripe].R, bytes[offset + 2]); Assert.Equal(255, bytes[offset + 3]);
            }
        }
    }
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Northpass.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository not found.");
    }
    private sealed class ProbeFixture : IServiceProbeTransport
    {
        public Task<ServiceProbeResult> ProbeAsync(ServiceProbeTarget target, CancellationToken token)
        {
            var passed = new ProbeObservation(ProbeState.Passed, "fixture only");
            var failed = new ProbeObservation(ProbeState.Failed, "fixture only");
            var unknown = new ProbeObservation(ProbeState.Unknown, "not tested");
            return Task.FromResult(new ServiceProbeResult(target.Id, target.Name, target.HttpsUrl.Host, ["203.0.113.1"], "203.0.113.1",
                passed, target.Id == "telegram" ? failed : passed, passed, target.Id == "discord" ? failed : passed,
                unknown, unknown, unknown, unknown, unknown, unknown));
        }
    }
    private sealed class DelayedProbeFixture : IServiceProbeTransport
    {
        public int Cancellations;
        public async Task<ServiceProbeResult> ProbeAsync(ServiceProbeTarget target, CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancellations); throw; }
            throw new InvalidOperationException("Cancelled fixture must not produce results.");
        }
    }
    private sealed class SetupConsent : IDesktopServices
    {
        public int Consents;
        public string? PickExport(string name, string extension) => throw new InvalidOperationException("Unexpected dialog.");
        public bool ConfirmTrust(string message) { Consents++; throw new InvalidOperationException("Automatic preparation must not prompt for component consent."); }
        public Task SetAutoStartAsync(bool enabled) => throw new InvalidOperationException("Unexpected autostart change.");
    }
    private sealed class SessionFixture(EngineDescriptor? metadata = null) : IDpiEngine
    {
        private EngineStatus _status = new(EngineState.Disconnected);
        public EngineDescriptor Descriptor => metadata ?? Zapret1Engine.Metadata;
        public event Action<string>? LogReceived;
        public event Action<EngineStatus>? StatusChanged;
        public Task StartAsync(EngineConfiguration configuration, CancellationToken token = default)
        {
            _status = new(EngineState.Active, 4242, DateTimeOffset.UtcNow);
            LogReceived?.Invoke("UI session fixture only; no process launched.");
            StatusChanged?.Invoke(_status); return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken token = default)
        { _status = new(EngineState.Disconnected); StatusChanged?.Invoke(_status); return Task.CompletedTask; }
        public async Task RestartAsync(EngineConfiguration configuration, CancellationToken token = default)
        { await StopAsync(token); await StartAsync(configuration, token); }
        public Task<EngineStatus> GetStatusAsync(CancellationToken token = default) => Task.FromResult(_status);
        public Task<ValidationResult> ValidateConfigurationAsync(EngineConfiguration configuration, CancellationToken token = default)
            => Task.FromResult(ValidationResult.Valid);
        public async ValueTask DisposeAsync() => await StopAsync();
    }
    // UI flow fixture only. Real executable/ACL checks are in FlowsealWindowsTests.
    private sealed class FakeInstallation(string engineId = "zapret1", bool waitForCancellation = false) : IEngineInstallationManager
    {
        public int Setups;
        private InstalledEngine? _engine;
        public string EngineId => engineId;
        public Task<InstalledEngine?> DetectAsync(CancellationToken token = default) => Task.FromResult(_engine);
        public async Task<InstalledEngine> EnsureInstalledAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default)
        {
            if (waitForCancellation) await Task.Delay(Timeout.Infinite, token);
            if (_engine is not null) return _engine;
            Setups++; _engine = new(engineId, new string('a', 40), "test", "C:\\protected\\winws.exe");
            progress?.Report(new("Ready", 1, 1)); return _engine;
        }
        public Task<EngineUpdateStatus> CheckForUpdatesAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstalledEngine> UpdateAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstalledEngine> RepairAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<InstalledEngine> RollbackAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<IAsyncDisposable> AcquireLaunchLeaseAsync(string path, CancellationToken token = default) => throw new InvalidOperationException("UI smoke test must not launch an engine.");
    }
}
