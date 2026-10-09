using System.Collections.ObjectModel;
using System.Windows.Threading;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Zapret1;
using Northpass.Models;
using Northpass.Services;

namespace Northpass.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly EngineController _controller;
    private readonly SettingsStore _settingsStore;
    private readonly ProfileStore _profileStore;
    private readonly UpdateChecker _updates;
    private readonly IDesktopServices _desktop;
    private readonly Dispatcher _dispatcher;
    private readonly IEngineInstallationManager _installation;
    private CancellationTokenSource? _diagnosticsCancellation;
    private Task? _diagnosticsTask;
    private long _diagnosticsEpoch;
    private bool _diagnosticsRunning;
    private string _appUpdateKey = "UpdateInfo";
    private readonly DataListStore? _dataLists;
    private readonly ServiceProbeService _serviceProbes;
    private readonly StrategyTestRecordStore? _testRecords;
    private CancellationTokenSource? _testCancellation;
    private Task? _testTask;
    private StrategyTestRecord? _lastTest;
    private string _providerLabel = "", _serviceResults = "";
    private string _gameTcpPorts = "12", _gameUdpPorts = "12";
    private string _generalListId = "", _excludedHostsId = "", _excludedIpsId = "", _allIpsId = "";
    private DataListKind _importListKind;
    private string _setupPhase = "SetupRequired", _engineRevision = "";
    private double _setupProgress;
    private Task? _setupTask;
    private Task? _startupUpdateTask;
    private CancellationTokenSource _lifetime = new();
    private AppSettings _settings = new();
    private bool _loading = true, _settingsReadable = true, _busy, _refreshing, _disposed, _shuttingDown;
    private bool _appliedAutoStart;
    private StrategyProfile? _selectedProfile;
    private string _enginePath = "", _language = "ru", _result = "", _log = "";
    private bool _tray = true, _autoStart, _recover, _checkUpdates;
    private EngineStatus _status = new(EngineState.Disconnected);
    private int _pageIndex;
    private readonly bool _developerTools;
    private bool _hasStartedSession;
    private string _messageKey = "Welcome";
    private readonly List<AsyncRelayCommand> _commands = new();

    public MainViewModel(EngineController controller, SettingsStore settingsStore,
        ProfileStore profileStore, UpdateChecker updates, IDesktopServices desktop, Dispatcher dispatcher,
        IEngineInstallationManager installation, ServiceProbeService serviceProbes, DataListStore? dataLists = null,
        StrategyTestRecordStore? testRecords = null, bool developerTools = false)
    {
        if (installation.EngineId != "zapret1") throw new ArgumentException("The product requires its reviewed network module.");
        (_controller, _settingsStore, _profileStore, _updates, _desktop, _dispatcher, _installation, _serviceProbes) =
            (controller, settingsStore, profileStore, updates, desktop, dispatcher, installation, serviceProbes);
        (_dataLists, _testRecords) = (dataLists, testRecords);
        _developerTools = developerTools;
        try { _language = settingsStore.Load().Language; } catch { /* InitializeAsync reports unreadable settings without overwriting them. */ }
        foreach (var target in ServiceProbeService.Targets) ServiceCards.Add(new(target, Language));
        ImportListCommand = Command(ImportListAsync, () => CanConfigure && _dataLists is not null);
        SaveInputsCommand = Command(SaveInputsAsync, () => CanConfigure);
        ServiceProbesCommand = new(RefreshServiceDiagnosticsAsync, ex => Fail(ex), () => !_disposed && !_shuttingDown && !DiagnosticsRunning && !Busy);
        OpenDiagnosticsCommand = new(() => PageIndex = 2);
        TestStrategyCommand = Command(TestStrategyAsync, () => SelectedProfile is not null && _serviceProbes is not null && _testRecords is not null);
        CancelTestCommand = new(() => _testCancellation?.Cancel(), () => _testCancellation is not null);
        NextStrategyCommand = Command(NextStrategyAsync, () => SelectedProfile is not null);
        ReportPlaybackPassedCommand = Command(() => ReportManualOutcome(true, ProbeState.Passed), () => _lastTest is not null);
        ReportPlaybackFailedCommand = Command(() => ReportManualOutcome(true, ProbeState.Failed), () => _lastTest is not null);
        ReportVoicePassedCommand = Command(() => ReportManualOutcome(false, ProbeState.Passed), () => _lastTest is not null);
        ReportVoiceFailedCommand = Command(() => ReportManualOutcome(false, ProbeState.Failed), () => _lastTest is not null);
        SetupEngineCommand = Command(SetupEngineAsync, () => CanConfigure);
        RepairEngineCommand = Command(RepairEngineAsync, () => CanConfigure);
        UpdateEngineCommand = Command(UpdateEngineAsync, () => CanConfigure);
        RollbackEngineCommand = Command(RollbackEngineAsync, () => CanConfigure);
        _controller.LogReceived += EngineLog;
        _controller.StatusChanged += EngineStatusChanged;
        ConnectCommand = Command(ToggleAsync, () => SelectedProfile is not null);
        ValidateCommand = Command(ValidateAsync, () => SelectedProfile is not null);
        RefreshProfilesCommand = Command(() => { ReloadProfiles(); return Task.CompletedTask; }, () => CanConfigure);
        ExportCommand = Command(ExportAsync, () => SelectedProfile is not null);
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
    public string[] Languages { get; } = ["ru", "en", "az"];
    public IReadOnlyList<LanguageChoice> LanguageChoices => LanguageChoice.All;
    public UiStrings Strings => new(Language);
    public int PageIndex { get => _pageIndex; set => Set(ref _pageIndex, value == 1 && !ShowAdvancedTools ? 0 : value); }
    public bool ShowAdvancedTools => _developerTools;
    public string UserMessage => Strings[_messageKey];
    public bool Preparing => _setupPhase is not ("Ready" or "SetupFailed");
    public string ConnectionNote => Strings[SessionOpen ? "ActiveNote" : "HomeNote"];
    public string ProfilesDirectory => _profileStore.DirectoryPath;
    public StrategyProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SessionOpen && !_loading) throw new InvalidOperationException("Disconnect before selecting another strategy.");
            if (!Set(ref _selectedProfile, value)) return;
            _lastTest = null; ServiceResults = "";
            InvalidateDiagnostics();
            Changed(nameof(SelectedStrategy));
            GameTcpPorts = value?.GameTcpPorts ?? "12"; GameUdpPorts = value?.GameUdpPorts ?? "12";
            GeneralListId = value?.ListBindings?.GetValueOrDefault("general") ?? "";
            ExcludedHostsId = value?.ListBindings?.GetValueOrDefault("excluded-hosts") ?? "";
            ExcludedIpsId = value?.ListBindings?.GetValueOrDefault("excluded-ips") ?? "";
            AllIpsId = value?.ListBindings?.GetValueOrDefault("all-ips") ?? "";
            Changed(nameof(ProfileDescription));
            if (!_loading) PersistSelection();
            RefreshCommands();
        }
    }
    public ObservableCollection<DataListEntry> HostLists { get; } = new();
    public ObservableCollection<DataListEntry> IpLists { get; } = new();
    public DataListKind[] ListKinds { get; } = [DataListKind.Hosts, DataListKind.IpSet];
    public DataListKind ImportListKind { get => _importListKind; set => Set(ref _importListKind, value); }
    public string GeneralListId { get => _generalListId; set => Set(ref _generalListId, value); }
    public string ExcludedHostsId { get => _excludedHostsId; set => Set(ref _excludedHostsId, value); }
    public string ExcludedIpsId { get => _excludedIpsId; set => Set(ref _excludedIpsId, value); }
    public string AllIpsId { get => _allIpsId; set => Set(ref _allIpsId, value); }
    public string GameTcpPorts { get => _gameTcpPorts; set => Set(ref _gameTcpPorts, value); }
    public string GameUdpPorts { get => _gameUdpPorts; set => Set(ref _gameUdpPorts, value); }
    public string ProviderLabel { get => _providerLabel; set => Set(ref _providerLabel, value); }
    public string ServiceResults { get => _serviceResults; private set => Set(ref _serviceResults, value); }
    public AsyncRelayCommand ImportListCommand { get; }
    public AsyncRelayCommand SaveInputsCommand { get; }
    public AsyncRelayCommand ServiceProbesCommand { get; }
    public AsyncRelayCommand TestStrategyCommand { get; }
    public AsyncRelayCommand NextStrategyCommand { get; }
    public RelayCommand CancelTestCommand { get; }
    public AsyncRelayCommand ReportPlaybackPassedCommand { get; }
    public AsyncRelayCommand ReportPlaybackFailedCommand { get; }
    public AsyncRelayCommand ReportVoicePassedCommand { get; }
    public AsyncRelayCommand ReportVoiceFailedCommand { get; }
    public string EnginePath { get => _enginePath; private set => Set(ref _enginePath, value); }
    public string EngineSetupText => Strings[_setupPhase];
    public double EngineSetupProgress { get => _setupProgress; private set => Set(ref _setupProgress, value); }
    public string EngineRevision { get => _engineRevision; private set => Set(ref _engineRevision, value); }
    public AsyncRelayCommand SetupEngineCommand { get; }
    public AsyncRelayCommand RepairEngineCommand { get; }
    public AsyncRelayCommand UpdateEngineCommand { get; }
    public AsyncRelayCommand RollbackEngineCommand { get; }
    public string Language
    {
        get => _language;
        set
        {
            if (!Languages.Contains(value)) throw new ArgumentException("Unsupported language.", nameof(value));
            if (!Set(ref _language, value)) return;
            foreach (var card in ServiceCards) card.Localize(value);
            foreach (var choice in Strategies) choice.Localize(value);
            Changed(nameof(AppUpdateText)); Changed(nameof(QuickDiagnostics));
            Changed(nameof(Strings)); Changed(nameof(EngineSetupText)); Changed(nameof(StatusText)); Changed(nameof(ConnectText)); Changed(nameof(UserMessage)); Changed(nameof(ConnectionNote));
            if (!_loading && _settingsReadable) { _settings.Language = value; PersistSelection(); }
        }
    }
    public bool MinimizeToTray { get => _tray; set => Set(ref _tray, value); }
    public bool StartWithWindows { get => _autoStart; set => Set(ref _autoStart, value); }
    public bool AutoRecover { get => _recover; set => Set(ref _recover, value); }
    public bool CheckForUpdates { get => _checkUpdates; set => Set(ref _checkUpdates, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { Changed(nameof(CanConfigure)); Changed(nameof(StatusText)); RefreshCommands(); } } }
    public bool SessionOpen => _status.State is EngineState.Active or EngineState.Connecting or EngineState.Stopping || _status.ProcessId is not null;
    public bool CanConfigure => !SessionOpen && !Busy;
    public string ConnectText => Strings[SessionOpen ? "Disconnect" : "Connect"];
    public string StatusText => Preparing || _setupPhase == "SetupFailed" ? EngineSetupText : Strings[_status.State == EngineState.Disconnected && _setupPhase == "Ready" && !_hasStartedSession ? "Ready" : _status.State.ToString()];
    public string StatusColor => _status.State switch { EngineState.Active => "#9DC5B1", EngineState.Error => "#DC998D", EngineState.Connecting or EngineState.Stopping => "#D2BE8F", _ => "#949EAA" };
    public string ProfileDescription => SelectedProfile?.Description ?? "Select or import a strategy.";
    public string SessionDuration => _status.State == EngineState.Active && _status.StartedAt is { } start
        ? (DateTimeOffset.UtcNow - start).ToString(@"hh\:mm\:ss") : "00:00:00";
    public string ResultText { get => _result; private set => Set(ref _result, value); }
    public string LogText { get => _log; private set => Set(ref _log, value); }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand ValidateCommand { get; }
    public AsyncRelayCommand RefreshProfilesCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public AsyncRelayCommand ExportLogsCommand { get; }
    public RelayCommand ClearLogsCommand { get; }

    public async Task InitializeAsync()
    {
        Busy = true;
        try
        {
            try { _settings = _settingsStore.Load(); }
            catch (Exception ex) { _settingsReadable = false; Fail(new IOException("Settings are unreadable and will not be overwritten. Restore settings.json before saving. " + ex.Message)); }
            Language = Languages.Contains(_settings.Language) ? _settings.Language : "ru";
            MinimizeToTray = _settings.MinimizeToTray;
            StartWithWindows = _appliedAutoStart = _settings.StartWithWindows;
            AutoRecover = _settings.AutoRecover;
            CheckForUpdates = _settings.CheckForUpdates;
            _profileStore.Seed(Path.Combine(AppContext.BaseDirectory, "profiles"));
            ReloadProfiles(string.IsNullOrEmpty(_settings.SelectedProfileId) ? FlowsealCatalog.StarterProfileId : null);
            ReloadLists();
            _loading = false;
            Log("Northpass 0.7. Engine process state and website reachability are reported separately.");
            {
                try
                {
                    var installed = await _installation.DetectAsync(_lifetime.Token);
                    if (installed is not null) ApplyInstalled(installed);
                    else await SetupEngineAsync();
                }
                catch (Exception ex) { SetSetupPhase("SetupFailed"); Fail(ex, "InstallationFailed"); }
            }

        }
        finally { Busy = false; }
        BeginDiagnostics();
        if (CheckForUpdates && !_shuttingDown) _startupUpdateTask = CheckStartupUpdatesAsync();
    }

    private async Task CheckStartupUpdatesAsync()
    {
        try { await CheckUpdatesAsync(); }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception ex) { if (!_shuttingDown) Fail(ex); }
    }

    private void ReloadProfiles(string? selectId = null)
    {
        string? id = selectId ?? SelectedProfile?.Id ?? _settings.SelectedProfileId;
        var result = _profileStore.Load();
        foreach (string error in result.Errors) Log("Profile error: " + error);
        Profiles.Clear();
        Strategies.Clear();
        foreach (var profile in result.Profiles)
        {
            var reviewed = FlowsealCatalog.Find(profile.StrategyId ?? "");
            if (profile.Engine != "zapret1" || reviewed is null || !profile.Arguments.SequenceEqual(FlowsealCatalog.Templates(reviewed)))
            { Log("Preserved an unsupported/modified profile without exposing it as a product strategy: " + profile.Id); continue; }
            Profiles.Add(profile);
        }
        if (!Profiles.Any(p => p.StrategyId == "general"))
        {
            var starter = FlowsealCatalog.Profile(FlowsealCatalog.Find("general")!);
            if (result.Profiles.Any(p => p.Id == starter.Id) || File.Exists(Path.Combine(_profileStore.DirectoryPath, starter.Id + ".json")))
                starter.Id = "reviewed-general-" + Guid.NewGuid().ToString("N")[..8];
            Profiles.Add(_profileStore.Save(starter));
        }
        foreach (var profile in Profiles.OrderBy(p => p.StrategyId == "general" ? 0 : 1).ThenBy(p => p.Id)) Strategies.Add(new(profile, Language));
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.First(p => p.StrategyId == "general");
        if (SelectedProfile.Id != id && !string.IsNullOrEmpty(id)) Log("Selection migrated to an included reviewed configuration; existing files/preferences were preserved.");
    }

    private EngineConfiguration Configuration() => new(EnginePath.Trim(),
        SelectedProfile ?? throw new InvalidOperationException("Select a strategy first."));

    private async Task ToggleAsync()
    {
        await CancelDiagnosticsAsync();
        if (SessionOpen) { await _controller.DisconnectAsync(_lifetime.Token); ApplyStatus(await _controller.GetStatusAsync(_lifetime.Token)); BeginDiagnostics(); return; }
        // End any pending crash recovery before preparing a manually requested session.
        await _controller.DisconnectAsync(_lifetime.Token);
        {
            try { if (!await EnsureEngineAsync()) return; }
            catch (Exception ex) { Fail(ex, "InstallationFailed"); return; }
        }
        var configuration = Configuration();
        var validation = await _controller.ValidateAsync(configuration, _lifetime.Token);
        ResultText = validation.Summary;
        if (!validation.IsValid) { Fail(new InvalidOperationException(validation.Summary), "ValidationFailed"); return; }
        PersistSelection();
        SetMessage("Welcome");
        try { await _controller.ConnectAsync(configuration, AutoRecover, _lifetime.Token); ApplyStatus(await _controller.GetStatusAsync(_lifetime.Token)); BeginDiagnostics(); }
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
        if (!_desktop.ConfirmTrust(Strings["EngineRepairConsent"])) return;
        await _controller.DisconnectAsync(_lifetime.Token);
        var task = _installation.RepairAsync(SetupProgress(), _lifetime.Token); _setupTask = task;
        try { ApplyInstalled(await task); }
        catch { SetSetupPhase("SetupFailed"); throw; }
        finally { _setupTask = null; }
    }
    private async Task RollbackEngineAsync()
    {
        if (!_desktop.ConfirmTrust(Strings["EngineRollbackConsent"])) return;
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
    private Task ExportAsync()
    {
        var profile = SelectedProfile ?? throw new InvalidOperationException("No profile selected.");
        string? path = _desktop.PickExport(profile.Id, "json");
        if (path is not null) { _profileStore.Export(profile, path); Log("Profile exported: " + path); }
        return Task.CompletedTask;
    }
    private void ReloadLists()
    {
        HostLists.Clear(); IpLists.Clear();
        HostLists.Add(new("", Strings["BundledDefault"], DataListKind.Hosts));
        IpLists.Add(new("", Strings["BundledDefault"], DataListKind.IpSet));
        foreach (var entry in _dataLists?.Load() ?? [])
            (entry.Kind == DataListKind.Hosts ? HostLists : IpLists).Add(entry);
    }
    private Task ImportListAsync()
    {
        string? file = _desktop.PickDataList();
        if (file is null) return Task.CompletedTask;
        var entry = _dataLists!.Import(file, ImportListKind);
        ReloadLists(); Log("Imported validated data-only list: " + entry.Name);
        return Task.CompletedTask;
    }
    private Task SaveInputsAsync()
    {
        if (!Zapret1Options.ValidPorts(GameTcpPorts) || !Zapret1Options.ValidPorts(GameUdpPorts))
            throw new InvalidDataException("Port lists must contain decimal ports or ordered ranges from 1 to 65535.");
        var original = SelectedProfile ?? throw new InvalidOperationException("Select a strategy first.");
        var replacement = ProfileValidation.Copy(original);
        replacement.GameTcpPorts = GameTcpPorts; replacement.GameUdpPorts = GameUdpPorts;
        replacement.ListBindings = new(StringComparer.Ordinal);
        foreach (var pair in new[] { ("general", GeneralListId), ("excluded-hosts", ExcludedHostsId), ("excluded-ips", ExcludedIpsId), ("all-ips", AllIpsId) })
            if (!string.IsNullOrEmpty(pair.Item2))
            {
                _dataLists!.Read(pair.Item2, pair.Item1 is "general" or "excluded-hosts" ? DataListKind.Hosts : DataListKind.IpSet);
                replacement.ListBindings.Add(pair.Item1, pair.Item2);
            }
        _profileStore.Update(original, replacement); ReloadProfiles(original.Id);
        SetMessage("SettingsSaved"); return Task.CompletedTask;
    }
    private string FormatServices(IReadOnlyList<ServiceProbeResult> results) => string.Join("\n\n", results.Select(r =>
        $"{r.Name} · DNS: {Strings[r.Dns.State.ToString()]} · TCP: {Strings[r.Tcp.State.ToString()]} · TLS: {Strings[r.Tls.State.ToString()]} · HTTPS: {Strings[r.Https.State.ToString()]}\n{Strings["ProbeScope"]}"));
    private async Task TestStrategyAsync()
    {
        await CancelDiagnosticsAsync();
        var selected = ProfileValidation.Copy(SelectedProfile!);
        _lastTest = null;
        if (!_desktop.ConfirmTrust(Strings["StrategyTestConsent"])) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _testCancellation = cancel; CancelTestCommand.Refresh();
        var runner = new StrategyTestRunner(_controller, _serviceProbes!, _testRecords!);
        var task = runner.RunAsync(selected, ProviderLabel, async (profile, token) =>
        {
            if (profile.Engine != _installation.EngineId) throw new InvalidOperationException("Unsupported product strategy.");
            var installed = await _installation.EnsureInstalledAsync(SetupProgress(), token);
            ApplyInstalled(installed);
            return (new EngineConfiguration(installed.ExecutablePath, profile), installed.Revision);
        }, cancel.Token);
        _testTask = task;
        try
        {
            _lastTest = await task;
            ServiceResults = FormatServices(_lastTest.Services);
            ApplyServiceResults(_lastTest.Services);
            Log("Strategy evidence saved: " + _lastTest.Id + ".json (no provider-wide or voice conclusion)");
        }
        finally { _testTask = null; _testCancellation = null; CancelTestCommand.Refresh(); }
    }
    private async Task NextStrategyAsync()
    {
        await CancelDiagnosticsAsync();
        await _controller.DisconnectAsync(_lifetime.Token);
        // This is an explicit user action; it never connects or chooses based on probe results.
        ApplyStatus(await _controller.GetStatusAsync(_lifetime.Token));
        var choices = Profiles.Where(p => p.Engine == "zapret1").ToArray();
        if (choices.Length == 0) return;
        int index = Array.FindIndex(choices, p => p.Id == SelectedProfile?.Id);
        SelectedProfile = choices[(index + 1) % choices.Length];
        _lastTest = null;
    }
    private Task ReportManualOutcome(bool playback, ProbeState outcome)
    {
        if (!_desktop.ConfirmTrust(Strings["ManualOutcomeConsent"])) return Task.CompletedTask;
        _lastTest = playback ? _lastTest! with { UserReportedPlayback = outcome } : _lastTest! with { UserReportedVoice = outcome };
        _testRecords!.Save(_lastTest); Log("User-reported observation saved separately from automated checks.");
        return Task.CompletedTask;
    }
    public ObservableCollection<StrategyChoice> Strategies { get; } = new();
    public StrategyChoice? SelectedStrategy
    {
        get => Strategies.FirstOrDefault(choice => ReferenceEquals(choice.Profile, SelectedProfile));
        set { if (value is not null && Strategies.Contains(value)) SelectedProfile = value.Profile; }
    }
    public ObservableCollection<ServiceDiagnosticCard> ServiceCards { get; } = new();
    public RelayCommand OpenDiagnosticsCommand { get; }
    public string AppUpdateText => Strings[_appUpdateKey];
    public string QuickDiagnostics => Strings[DiagnosticsRunning ? "CheckingServices" : "DiagnosticsSummary"];
    public bool DiagnosticsRunning
    {
        get => _diagnosticsRunning;
        private set { if (Set(ref _diagnosticsRunning, value)) { Changed(nameof(QuickDiagnostics)); ServiceProbesCommand.Refresh(); } }
    }
    private void InvalidateDiagnostics()
    {
        _diagnosticsEpoch++; _diagnosticsCancellation?.Cancel();
        foreach (var card in ServiceCards) card.Apply(null);
        ServiceResults = "";
    }
    private async Task CancelDiagnosticsAsync()
    {
        InvalidateDiagnostics();
        if (_diagnosticsTask is { } task) await task;
    }
    private void BeginDiagnostics()
    {
        if (!_disposed && !_shuttingDown && _setupPhase == "Ready" && !DiagnosticsRunning && _status.State is not (EngineState.Connecting or EngineState.Stopping))
            _ = RefreshServiceDiagnosticsAsync(); // The task is tracked and exceptions handled below.
    }
    public async Task RefreshServiceDiagnosticsAsync()
    {
        if (_disposed || _shuttingDown || DiagnosticsRunning) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _diagnosticsCancellation = cancel;
        long epoch = _diagnosticsEpoch;
        DiagnosticsRunning = true;
        foreach (var card in ServiceCards) card.Apply(null);
        var task = RunDiagnosticsAsync(epoch, cancel.Token); _diagnosticsTask = task;
        try { await task; }
        finally
        {
            _diagnosticsTask = null; _diagnosticsCancellation = null; DiagnosticsRunning = false;
            if (epoch != _diagnosticsEpoch && !Busy) BeginDiagnostics();
        }
    }
    private async Task RunDiagnosticsAsync(long epoch, CancellationToken token)
    {
        try
        {
            var snapshot = await _controller.GetStatusAsync(token);
            var results = await _serviceProbes.TestAsync(token);
            var current = await _controller.GetStatusAsync(token);
            if (epoch != _diagnosticsEpoch || snapshot.State != current.State || snapshot.ProcessId != current.ProcessId) return;
            ApplyServiceResults(results);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("Service diagnostics failed: " + ex.Message); }
    }
    private void ApplyServiceResults(IReadOnlyList<ServiceProbeResult> results)
    {
        foreach (var result in results)
        {
            ServiceCards.Single(card => card.Id == result.Id).Apply(result);
            Log(System.Text.Json.JsonSerializer.Serialize(result));
        }
        ServiceResults = FormatServices(results);
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
        var result = await _updates.CheckAsync(_lifetime.Token);
        _appUpdateKey = result.State.ToString(); Changed(nameof(AppUpdateText));
        ResultText = result.Detail; Log(ResultText);
        Log((await _installation.CheckForUpdatesAsync(_lifetime.Token)).Detail);
    }
    private async Task ExportLogsAsync()
    {
        if (await _controller.GetPerformanceAsync(_lifetime.Token) is { } performance)
            Log("Native performance: " + System.Text.Json.JsonSerializer.Serialize(performance));
        string? path = _desktop.PickExport("Northpass-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), "txt");
        if (path is not null) { File.WriteAllText(path, LogText); Log("Diagnostics exported. Review paths and hostnames before sharing."); }
    }
    private void RefreshCommands() { foreach (var command in _commands) command.Refresh(); CancelTestCommand.Refresh(); ServiceProbesCommand.Refresh(); }
    private void SetMessage(string key) { _messageKey = key; Changed(nameof(UserMessage)); }
    public void Fail(Exception ex, string messageKey = "ActionFailed")
    {
        ResultText = ex.Message; Log("Error: " + ex.Message);
        if (messageKey == "InstallationFailed" && (ex is FileNotFoundException ||
            ex is InvalidDataException && (ex.Message.Contains("acquisition.zip", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Offline engine payload", StringComparison.OrdinalIgnoreCase)))) messageKey = "ReinstallRequired";
        if (ex is Northpass.Broker.ElevationDeclinedException) messageKey = "PermissionDeclined";
        if (ex is EngineConflictException) messageKey = "EngineConflict";
        if (ex is UnauthorizedAccessException) messageKey = "AccessDenied";
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
    private void EngineStatusChanged(EngineStatus status) => _dispatcher.BeginInvoke(new Action(async () => { if (!_disposed && !_shuttingDown) await RefreshAsync(); }));
    private void ApplyStatus(EngineStatus status)
    {
        if (_status.State != status.State || _status.ProcessId != status.ProcessId) InvalidateDiagnostics();
        _status = status;
        if (status.State == EngineState.Active) _hasStartedSession = true;
        foreach (string name in new[] { nameof(StatusText), nameof(StatusColor), nameof(SessionOpen), nameof(CanConfigure), nameof(ConnectText), nameof(SessionDuration), nameof(ConnectionNote) }) Changed(name);
        if (status.Error is not null) Fail(new IOException(status.Error), "ConnectionFailed");
        RefreshCommands();
    }
    public async Task RefreshAsync()
    {
        if (_disposed || _shuttingDown || _refreshing) return;
        _refreshing = true;
        try
        {
            var current = await _controller.GetStatusAsync(_lifetime.Token);
            bool changed = current.State != _status.State || current.ProcessId != _status.ProcessId;
            ApplyStatus(current);
            if (changed && !Busy) BeginDiagnostics();
        }
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
        await CancelDiagnosticsAsync();
        if (_startupUpdateTask is { } updates) await updates;
        if (_testTask is { } test)
        { try { await test; } catch (Exception ex) { Log("Strategy test ended during shutdown: " + ex.Message); } }
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
