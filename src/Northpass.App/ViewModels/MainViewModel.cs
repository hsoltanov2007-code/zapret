using System.Collections.ObjectModel;
using System.Windows.Threading;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Models;
using Northpass.Services;

namespace Northpass.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly EngineController _controller;
    private readonly EngineRegistry _registry;
    private readonly SettingsStore _settingsStore;
    private readonly ProfileStore _profileStore;
    private readonly DiagnosticsService _diagnostics;
    private readonly UpdateChecker _updates;
    private readonly IDesktopServices _desktop;
    private readonly Dispatcher _dispatcher;
    private readonly IEngineInstallationManager? _installation;
    private string _setupPhase = "SetupRequired", _engineRevision = "";
    private double _setupProgress;
    private Task? _setupTask;
    private CancellationTokenSource _lifetime = new();
    private AppSettings _settings = new();
    private bool _loading = true, _settingsReadable = true, _busy, _refreshing, _disposed, _shuttingDown;
    private bool _appliedAutoStart;
    private StrategyProfile? _selectedProfile;
    private string _enginePath = "", _language = "en", _url = "https://example.com/", _result = "", _log = "";
    private bool _tray = true, _autoStart, _recover, _checkUpdates;
    private EngineStatus _status = new(EngineState.Disconnected);
    private ReachabilityResult? _reachability;
    private int _pageIndex;
    private bool _showAdvanced;
    private string _messageKey = "Welcome";
    private readonly List<AsyncRelayCommand> _commands = new();

    public MainViewModel(EngineController controller, EngineRegistry registry, SettingsStore settingsStore,
        ProfileStore profileStore, DiagnosticsService diagnostics, UpdateChecker updates, IDesktopServices desktop, Dispatcher dispatcher, IEngineInstallationManager? installation = null)
    {
        (_controller, _registry, _settingsStore, _profileStore, _diagnostics, _updates, _desktop, _dispatcher) =
            (controller, registry, settingsStore, profileStore, diagnostics, updates, desktop, dispatcher);
        _installation = installation;
        SetupEngineCommand = Command(SetupEngineAsync, () => CanConfigure);
        RepairEngineCommand = Command(RepairEngineAsync, () => CanConfigure);
        UpdateEngineCommand = Command(UpdateEngineAsync, () => CanConfigure);
        RollbackEngineCommand = Command(RollbackEngineAsync, () => CanConfigure);
        _controller.LogReceived += EngineLog;
        _controller.StatusChanged += EngineStatusChanged;
        ConnectCommand = Command(ToggleAsync, () => SelectedProfile is not null);
        BrowseCommand = Command(() => { EnginePath = _desktop.PickEngine(_registry.Find(SelectedProfile?.Engine ?? "") ?? _registry.Available.First()) ?? EnginePath; return Task.CompletedTask; }, () => CanConfigure && !ManagedEngine);
        ValidateCommand = Command(ValidateAsync, () => SelectedProfile is not null);
        RefreshProfilesCommand = Command(() => { ReloadProfiles(); return Task.CompletedTask; }, () => CanConfigure);
        NewProfileCommand = Command(() => EditProfile(null), () => CanConfigure);
        EditProfileCommand = Command(() => EditProfile(SelectedProfile), () => CanConfigure && SelectedProfile is not null);
        ImportCommand = Command(ImportAsync, () => CanConfigure);
        ExportCommand = Command(ExportAsync, () => SelectedProfile is not null);
        TestCommand = Command(TestAsync);
        SaveSettingsCommand = Command(SaveSettingsAsync, () => CanConfigure);
        CheckUpdatesCommand = Command(CheckUpdatesAsync);
        ExportLogsCommand = Command(ExportLogsAsync);
        ClearLogsCommand = new(() => LogText = "");
    }

    private AsyncRelayCommand Command(Func<Task> action, Func<bool>? condition = null)
    {
        var command = new AsyncRelayCommand(async () =>
        {
            Busy = true;
            try { await action(); }
            finally { Busy = false; }
        }, ex => Fail(ex), () => !_disposed && !_shuttingDown && !Busy && (condition?.Invoke() ?? true));
        _commands.Add(command);
        return command;
    }

    public ObservableCollection<StrategyProfile> Profiles { get; } = new();
    public string[] Languages { get; } = ["en", "ru", "az"];
    public UiStrings Strings => new(Language);
    public int PageIndex { get => _pageIndex; set => Set(ref _pageIndex, value); }
    public bool ShowAdvancedTools
    {
        get => _showAdvanced;
        set { if (Set(ref _showAdvanced, value) && !value && PageIndex == 1) PageIndex = 0; }
    }
    public string UserMessage => Strings[_messageKey];
    public bool Preparing => _installation is not null && _setupPhase is not ("Ready" or "SetupFailed");
    public string ConnectionNote => Strings[SessionOpen ? "ActiveNote" : "HomeNote"];
    public string ProfilesDirectory => _profileStore.DirectoryPath;
    public StrategyProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value)) return;
            Changed(nameof(CurrentEngine)); Changed(nameof(ProfileDescription)); Changed(nameof(ManagedEngine)); Changed(nameof(CanChooseEngine));
            _reachability = null; Changed(nameof(Reachability));
            if (!_loading) PersistSelection();
            RefreshCommands();
        }
    }
    public string EnginePath { get => _enginePath; set => Set(ref _enginePath, value); }
    public bool ManagedEngine => _installation is not null && string.Equals(SelectedProfile?.Engine, _installation.EngineId, StringComparison.OrdinalIgnoreCase);
    public bool CanChooseEngine => CanConfigure && !ManagedEngine;
    public string EngineSetupText => Strings[_setupPhase];
    public double EngineSetupProgress { get => _setupProgress; private set => Set(ref _setupProgress, value); }
    public string EngineRevision { get => _engineRevision; private set => Set(ref _engineRevision, value); }
    public AsyncRelayCommand SetupEngineCommand { get; }
    public AsyncRelayCommand RepairEngineCommand { get; }
    public AsyncRelayCommand UpdateEngineCommand { get; }
    public AsyncRelayCommand RollbackEngineCommand { get; }
    public string DiagnosticUrl { get => _url; set => Set(ref _url, value); }
    public string Language
    {
        get => _language;
        set
        {
            if (!Set(ref _language, value)) return;
            Changed(nameof(Strings)); Changed(nameof(EngineSetupText)); Changed(nameof(StatusText)); Changed(nameof(ConnectText)); Changed(nameof(Reachability)); Changed(nameof(UserMessage)); Changed(nameof(ConnectionNote));
        }
    }
    public bool MinimizeToTray { get => _tray; set => Set(ref _tray, value); }
    public bool StartWithWindows { get => _autoStart; set => Set(ref _autoStart, value); }
    public bool AutoRecover { get => _recover; set => Set(ref _recover, value); }
    public bool CheckForUpdates { get => _checkUpdates; set => Set(ref _checkUpdates, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { Changed(nameof(CanConfigure)); Changed(nameof(CanChooseEngine)); Changed(nameof(StatusText)); RefreshCommands(); } } }
    public bool SessionOpen => _status.State is EngineState.Active or EngineState.Connecting or EngineState.Stopping || _status.ProcessId is not null;
    public bool CanConfigure => !SessionOpen && !Busy;
    public string ConnectText => Strings[SessionOpen ? "Disconnect" : "Connect"];
    public string StatusText => Preparing || _setupPhase == "SetupFailed" ? EngineSetupText : Strings[_status.State == EngineState.Disconnected && _setupPhase == "Ready" ? "Ready" : _status.State.ToString()];
    public string StatusColor => _status.State switch { EngineState.Active => "#9DC5B1", EngineState.Error => "#DC998D", EngineState.Connecting or EngineState.Stopping => "#D2BE8F", _ => "#949EAA" };
    public string CurrentEngine => _registry.Find(SelectedProfile?.Engine ?? "")?.Name ?? "Unavailable";
    public string ProfileDescription => SelectedProfile?.Description ?? "Select or import a strategy.";
    public string SessionDuration => _status.State == EngineState.Active && _status.StartedAt is { } start
        ? (DateTimeOffset.UtcNow - start).ToString(@"hh\:mm\:ss") : "00:00:00";
    public string Reachability => _reachability is null ? Strings["Unverified"] :
        $"{Strings[_reachability.Reachable ? "Reachable" : "Unreachable"]} · {_reachability.Duration.TotalMilliseconds:F0} ms";
    public string ResultText { get => _result; private set => Set(ref _result, value); }
    public string LogText { get => _log; private set => Set(ref _log, value); }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand BrowseCommand { get; }
    public AsyncRelayCommand ValidateCommand { get; }
    public AsyncRelayCommand RefreshProfilesCommand { get; }
    public AsyncRelayCommand NewProfileCommand { get; }
    public AsyncRelayCommand EditProfileCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand TestCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public AsyncRelayCommand ExportLogsCommand { get; }
    public RelayCommand ClearLogsCommand { get; }

    public async Task InitializeAsync(string? discoveredEngine)
    {
        Busy = true;
        try
        {
            try { _settings = _settingsStore.Load(); }
            catch (Exception ex) { _settingsReadable = false; Fail(new IOException("Settings are unreadable and will not be overwritten. Restore settings.json before saving. " + ex.Message)); }
            EnginePath = string.IsNullOrWhiteSpace(_settings.EnginePath) ? discoveredEngine ?? "" : _settings.EnginePath;
            Language = Languages.Contains(_settings.Language) ? _settings.Language : "en";
            DiagnosticUrl = _settings.DiagnosticUrl;
            MinimizeToTray = _settings.MinimizeToTray;
            StartWithWindows = _appliedAutoStart = _settings.StartWithWindows;
            AutoRecover = _settings.AutoRecover;
            CheckForUpdates = _settings.CheckForUpdates;
            ShowAdvancedTools = _settings.ShowAdvancedTools;
            _profileStore.Seed(Path.Combine(AppContext.BaseDirectory, "profiles"));
            ReloadProfiles(string.IsNullOrEmpty(_settings.SelectedProfileId) ? "zapret2-reviewed-example" : null);
            // Preserve drafts as files, but do not make upgrades require JSON editing
            // before the first connection. A valid custom selection is kept.
            if (ManagedEngine && SelectedProfile?.Arguments.Count == 0 && Profiles.FirstOrDefault(p => p.Id == "zapret2-reviewed-example") is { } included)
            {
                Log("The selected profile was an empty draft; selected the included configuration without changing the draft.");
                SelectedProfile = included;
            }
            _loading = false;
            Log("Northpass 0.4. Engine process state and website reachability are reported separately.");
            if (_installation is not null)
            {
                try
                {
                    var installed = await _installation.DetectAsync(_lifetime.Token);
                    if (installed is not null) ApplyInstalled(installed);
                    else await SetupEngineAsync();
                }
                catch (Exception ex) { SetSetupPhase("SetupFailed"); Fail(ex, "InstallationFailed"); }
            }
            if (CheckForUpdates)
            {
                try { await CheckUpdatesAsync(); }
                catch (Exception ex) { Fail(ex); }
            }
        }
        finally { Busy = false; }
    }

    private void ReloadProfiles(string? selectId = null)
    {
        string? id = selectId ?? SelectedProfile?.Id ?? _settings.SelectedProfileId;
        var result = _profileStore.Load();
        foreach (string error in result.Errors) Log("Profile error: " + error);
        Profiles.Clear();
        foreach (var profile in result.Profiles) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault();
    }

    private EngineConfiguration Configuration() => new(EnginePath.Trim(),
        SelectedProfile ?? throw new InvalidOperationException("Select a strategy first."));

    private async Task ToggleAsync()
    {
        if (SessionOpen) { await _controller.DisconnectAsync(_lifetime.Token); return; }
        // End any pending crash recovery before preparing a manually requested session.
        await _controller.DisconnectAsync(_lifetime.Token);
        if (ManagedEngine)
        {
            try { if (!await EnsureEngineAsync()) return; }
            catch (Exception ex) { Fail(ex, "InstallationFailed"); return; }
        }
        var configuration = Configuration();
        var validation = await _controller.ValidateAsync(configuration, _lifetime.Token);
        ResultText = validation.Summary;
        if (!validation.IsValid) { Fail(new InvalidOperationException(validation.Summary), "ValidationFailed"); return; }
        if (!ManagedEngine && !_desktop.ConfirmTrust("Start the selected executable and profile with administrator rights?\n\nUse only an official trusted engine bundle. Lua files are executable code and can access your computer. Imported profiles are not sandboxed.\n\n" + configuration.ExecutablePath + "\n" + configuration.Profile.SourcePath)) return;
        PersistSelection();
        _reachability = null; Changed(nameof(Reachability));
        SetMessage("Welcome");
        try { await _controller.ConnectAsync(configuration, AutoRecover, _lifetime.Token); }
        catch (Exception ex) { Fail(ex, "ConnectionFailed"); }
    }

    private void SetSetupPhase(string phase) { _setupPhase = phase; Changed(nameof(EngineSetupText)); Changed(nameof(Preparing)); Changed(nameof(StatusText)); }
    private IProgress<InstallationProgress> SetupProgress() => new Progress<InstallationProgress>(p =>
    {
        if (!_disposed && !_shuttingDown) { SetSetupPhase(p.Phase); EngineSetupProgress = p.Percent; }
    });
    private void ApplyInstalled(InstalledEngine engine)
    {
        EnginePath = engine.ExecutablePath; EngineRevision = engine.Version + " · " + engine.Revision;
        SetSetupPhase("Ready"); EngineSetupProgress = 100;
        SetMessage("Welcome");
        PersistSelection();
    }
    private async Task<bool> EnsureEngineAsync()
    {
        if (_installation is null) return true;
        // Installing the bundled module is part of installing Northpass. v0.3's separate
        // component consent remains in settings for backwards compatibility only.
        SetSetupPhase("Extracting");
        try
        {
            var task = _installation.EnsureInstalledAsync(SetupProgress(), _lifetime.Token);
            _setupTask = task;
            ApplyInstalled(await task);
            return true;
        }
        catch { SetSetupPhase("SetupFailed"); throw; }
        finally { _setupTask = null; }
    }
    private async Task SetupEngineAsync()
    {
        await _controller.DisconnectAsync(_lifetime.Token);
        await EnsureEngineAsync();
    }
    private async Task UpdateEngineAsync()
    {
        if (_installation is null) return;
        await _controller.DisconnectAsync(_lifetime.Token);
        var update = await _installation.CheckForUpdatesAsync(_lifetime.Token);
        ResultText = update.Detail;
        if (!update.CanUpdate || !_desktop.ConfirmTrust(Strings["EngineUpdateConsent"])) return;
        var task = _installation.UpdateAsync(SetupProgress(), _lifetime.Token); _setupTask = task;
        try { ApplyInstalled(await task); }
        finally { _setupTask = null; }
    }
    private async Task RepairEngineAsync()
    {
        if (_installation is null || !_desktop.ConfirmTrust(Strings["EngineRepairConsent"])) return;
        await _controller.DisconnectAsync(_lifetime.Token);
        var task = _installation.RepairAsync(SetupProgress(), _lifetime.Token); _setupTask = task;
        try { ApplyInstalled(await task); }
        catch { SetSetupPhase("SetupFailed"); throw; }
        finally { _setupTask = null; }
    }
    private async Task RollbackEngineAsync()
    {
        if (_installation is null || !_desktop.ConfirmTrust(Strings["EngineRollbackConsent"])) return;
        await _controller.DisconnectAsync(_lifetime.Token);
        var task = _installation.RollbackAsync(_lifetime.Token); _setupTask = task;
        try { ApplyInstalled(await task); }
        finally { _setupTask = null; }
    }
    private async Task ValidateAsync()
    {
        ResultText = (await _controller.ValidateAsync(Configuration(), _lifetime.Token)).Summary;
        Log(ResultText);
    }
    private Task EditProfile(StrategyProfile? original)
    {
        var profile = _desktop.EditProfile(_profileStore, original);
        if (profile is not null) ReloadProfiles(profile.Id);
        return Task.CompletedTask;
    }
    private Task ImportAsync()
    {
        string? file = _desktop.PickProfile();
        if (file is null) return Task.CompletedTask;
        if (!_desktop.ConfirmTrust("Import this profile? It will not run now. Review all arguments and referenced Lua files before connecting. Duplicate Ids will not overwrite existing profiles.")) return Task.CompletedTask;
        var profile = _profileStore.Import(file);
        ReloadProfiles(profile.Id);
        Log("Profile imported. Copy referenced assets separately and verify their paths before connecting.");
        return Task.CompletedTask;
    }
    private Task ExportAsync()
    {
        var profile = SelectedProfile ?? throw new InvalidOperationException("No profile selected.");
        string? path = _desktop.PickExport(profile.Id, "json");
        if (path is not null) { _profileStore.Export(profile, path); Log("Profile exported: " + path); }
        return Task.CompletedTask;
    }
    private async Task TestAsync()
    {
        _reachability = await _diagnostics.TestAsync(DiagnosticUrl, _lifetime.Token);
        Changed(nameof(Reachability));
        ResultText = $"{_reachability.Url} · {Reachability}\n{_reachability.Detail}";
        Log(ResultText);
    }
    private void PersistSelection()
    {
        if (!_settingsReadable) { Log("Settings are unreadable; selection was not persisted."); return; }
        _settings.EnginePath = EnginePath.Trim();
        _settings.SelectedProfileId = SelectedProfile?.Id ?? "";
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) { Fail(ex); }
    }
    private async Task SaveSettingsAsync()
    {
        if (!_settingsReadable) throw new IOException("Restore settings.json before saving; the corrupt file has been preserved.");
        if (StartWithWindows != _appliedAutoStart)
        {
            await _desktop.SetAutoStartAsync(StartWithWindows);
            _appliedAutoStart = StartWithWindows;
        }
        _settings.EnginePath = EnginePath.Trim();
        _settings.SelectedProfileId = SelectedProfile?.Id ?? "";
        _settings.Language = Language;
        _settings.ShowAdvancedTools = ShowAdvancedTools;
        _settings.DiagnosticUrl = DiagnosticUrl;
        _settings.MinimizeToTray = MinimizeToTray;
        _settings.StartWithWindows = StartWithWindows;
        _settings.AutoRecover = AutoRecover;
        _settings.CheckForUpdates = CheckForUpdates;
        _settingsStore.Save(_settings);
        ResultText = "Settings saved.";
        SetMessage("SettingsSaved");
        Log(ResultText);
    }
    private async Task CheckUpdatesAsync()
    {
        ResultText = await _updates.CheckAsync(_lifetime.Token);
        if (_installation is not null) ResultText += "\n" + (await _installation.CheckForUpdatesAsync(_lifetime.Token)).Detail;
        Log(ResultText);
    }
    private Task ExportLogsAsync()
    {
        string? path = _desktop.PickExport("Northpass-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), "txt");
        if (path is not null) { File.WriteAllText(path, LogText); Log("Diagnostics exported. Review paths and hostnames before sharing."); }
        return Task.CompletedTask;
    }
    private void RefreshCommands() { foreach (var command in _commands) command.Refresh(); }
    private void SetMessage(string key) { _messageKey = key; Changed(nameof(UserMessage)); }
    public void Fail(Exception ex, string messageKey = "ActionFailed")
    {
        ResultText = ex.Message; Log("Error: " + ex.Message);
        if (messageKey == "ConnectionFailed" && (ex.Message.Contains("driver", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("windivert", StringComparison.OrdinalIgnoreCase)))
            messageKey = "NetworkBlocked";
        SetMessage(messageKey);
    }
    public void Log(string text)
    {
        LogText += $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}";
        if (LogText.Length > 200000) LogText = LogText[^150000..];
    }
    private void EngineLog(string text) => _dispatcher.BeginInvoke(() => { if (!_disposed) Log(text); });
    private void EngineStatusChanged(EngineStatus status) => _dispatcher.BeginInvoke(() => { if (!_disposed) ApplyStatus(status); });
    private void ApplyStatus(EngineStatus status)
    {
        if (_status.State != status.State) { _reachability = null; Changed(nameof(Reachability)); }
        _status = status;
        foreach (string name in new[] { nameof(StatusText), nameof(StatusColor), nameof(SessionOpen), nameof(CanConfigure), nameof(CanChooseEngine), nameof(ConnectText), nameof(SessionDuration), nameof(ConnectionNote) }) Changed(name);
        if (status.Error is not null) Fail(new IOException(status.Error), "ConnectionFailed");
        RefreshCommands();
    }
    public async Task RefreshAsync()
    {
        if (_disposed || _shuttingDown || _refreshing) return;
        _refreshing = true;
        try { ApplyStatus(await _controller.GetStatusAsync(_lifetime.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex); }
        finally { _refreshing = false; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _shuttingDown = true;
        RefreshCommands();
        _lifetime.Cancel();
        if (_setupTask is { } setup)
        {
            try { await setup; }
            catch (Exception ex) { Log("Engine setup ended during shutdown: " + ex.Message); }
        }
        try { await _controller.DisposeAsync(); }
        catch { _lifetime.Dispose(); _lifetime = new(); _shuttingDown = false; RefreshCommands(); throw; }
        _controller.LogReceived -= EngineLog;
        _controller.StatusChanged -= EngineStatusChanged;
        _disposed = true;
        _lifetime.Dispose();
    }
}
