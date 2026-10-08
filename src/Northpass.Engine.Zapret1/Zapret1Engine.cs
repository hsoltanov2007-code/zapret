using System.Diagnostics;
using System.Runtime.InteropServices;
using Northpass.Models;

namespace Northpass.Engine.Zapret1;

public sealed class Zapret1Engine : IDpiEngine
{
    public static EngineDescriptor Metadata { get; } = new("zapret1", "Flowseal / Zapret1", "winws.exe");
    private readonly IEngineInstallationManager _installation;
    private readonly IEngineDataProvider _data;
    private readonly IEngineCollisionDetector _collisions;
    private readonly bool _noTrafficCapture;
    private readonly ProcessSupervisor _process = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private IAsyncDisposable? _engineLease;
    private IEngineDataLease? _dataLease;
    private EngineStatus? _preflightFailure;
    private bool _disposed;
    public EngineDescriptor Descriptor => Metadata;
    public event Action<string>? LogReceived;
    public event Action<EngineStatus>? StatusChanged;
    public Zapret1Engine(IEngineInstallationManager installation, IEngineDataProvider data,
        IEngineCollisionDetector? collisions = null, bool noTrafficCapture = false)
    {
        if (installation.EngineId != Metadata.Id) throw new ArgumentException("A Zapret1 installation manager is required.");
        (_installation, _data, _collisions, _noTrafficCapture) = (installation, data, collisions ?? new EngineCollisionDetector(), noTrafficCapture);
        _process.LogReceived += text => LogReceived?.Invoke(text);
        _process.StatusChanged += state => { _preflightFailure = null; StatusChanged?.Invoke(state); };
    }
    public async Task<ValidationResult> ValidateConfigurationAsync(EngineConfiguration configuration, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var validation = Zapret1ConfigurationValidator.Validate(configuration);
        if (!validation.IsValid) return validation;
        var data = await _data.ValidateAsync(configuration.Profile, token);
        return new(validation.Issues.Concat(data.Issues).ToArray());
    }
    public async Task StartAsync(EngineConfiguration configuration, CancellationToken token = default)
    {
        await _operations.WaitAsync(token);
        try { await StartCoreAsync(configuration, token); }
        finally { _operations.Release(); }
    }
    private async Task StartCoreAsync(EngineConfiguration configuration, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((await _process.GetStatusAsync(token)).ProcessId is not null) throw new InvalidOperationException("Disconnect the owned session before starting another.");
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Flowseal Zapret1 requires Windows x64 and administrator privileges.");
            _collisions.Check();
            var validation = await ValidateConfigurationAsync(configuration, token);
            if (!validation.IsValid) throw new InvalidOperationException(validation.Summary);
            await ReleaseLeasesAsync();
            _engineLease = await _installation.AcquireLaunchLeaseAsync(configuration.ExecutablePath, token);
            _dataLease = await _data.PrepareAsync(configuration.Profile, token);
            foreach (string path in Directory.EnumerateFiles(_dataLease.DirectoryPath).OrderBy(p => p, StringComparer.Ordinal))
            {
                using var input = File.OpenRead(path);
                string hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token)).ToLowerInvariant();
                LogReceived?.Invoke("[data snapshot] " + Path.GetFileName(path) + " SHA-256 " + hash);
            }
            var info = CreateStartInfo(configuration, _dataLease, _noTrafficCapture);
            await ProbeAsync(info, ["--version"], token);
            await ProbeAsync(info, info.ArgumentList.Append("--dry-run").ToArray(), token);
            _collisions.Check(); // Recheck immediately before interception; never kill a collision.
            _preflightFailure = null;
            await _process.StartAsync(info, token);
        }
        catch (Exception ex)
        {
            var state = await _process.GetStatusAsync();
            if (state.ProcessId is null) await ReleaseLeasesAsync();
            _preflightFailure = state.ProcessId is null ? new(EngineState.Error, Error: ex.Message) : null;
            StatusChanged?.Invoke(_preflightFailure ?? state);
            throw;
        }
    }
    // ArgumentList and a verified PE path are the only execution route. No cmd,
    // powershell, BAT interpreter, PATH lookup, or environment expansion.
    public static ProcessStartInfo CreateStartInfo(EngineConfiguration configuration, IEngineDataLease data, bool noTrafficCapture = false)
    {
        var strategy = FlowsealCatalog.Find(configuration.Profile.StrategyId) ?? throw new InvalidDataException("Unreviewed strategy.");
        string exe = Path.GetFullPath(configuration.ExecutablePath), bin = Path.GetDirectoryName(exe)!;
        var info = new ProcessStartInfo(exe) { WorkingDirectory = bin, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in FlowsealCatalog.Compile(strategy, bin, Path.Combine(Path.GetDirectoryName(bin)!, "lists"),
            data.DirectoryPath, configuration.Profile.GameTcpPorts, configuration.Profile.GameUdpPorts, noTrafficCapture, data.AllIpsPath))
            info.ArgumentList.Add(argument);
        EngineProcessEnvironment.Harden(info);
        return info;
    }
    private async Task ProbeAsync(ProcessStartInfo template, IReadOnlyList<string> arguments, CancellationToken token)
    {
        string text = await RunProbeAsync(template, arguments, token);
        if (text.Length > 0) LogReceived?.Invoke("[preflight] " + text);
    }
    private static async Task<string> RunProbeAsync(ProcessStartInfo template, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(template.FileName) { WorkingDirectory = template.WorkingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear(); foreach (var item in template.Environment) info.Environment[item.Key] = item.Value;
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Verified engine preflight could not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        string text = (await output + "\n" + await error).Trim();
        if (text.Length > 65536) text = text[^65536..];
        if (process.ExitCode != 0) throw new InvalidOperationException($"Zapret1 preflight failed (code {process.ExitCode}): {text}");
        return text;
    }
    public static async Task VerifyInstalledVersionAsync(InstalledEngine installed, CancellationToken token)
    {
        var info = new ProcessStartInfo(installed.ExecutablePath) { WorkingDirectory = Path.GetDirectoryName(installed.ExecutablePath)! };
        EngineProcessEnvironment.Harden(info);
        string text = await RunProbeAsync(info, ["--version"], token);
        if (!text.Contains("github version v" + installed.Version + " (" + installed.SourceRevision + ")", StringComparison.Ordinal))
            throw new InvalidDataException("Zapret1 version/source revision differs from the reviewed manifest: " + text);
    }
    public async Task StopAsync(CancellationToken token = default)
    {
        await _operations.WaitAsync(token);
        try { await _process.StopAsync(token); await ReleaseLeasesAsync(); _preflightFailure = null; }
        finally { _operations.Release(); }
    }
    public async Task RestartAsync(EngineConfiguration configuration, CancellationToken token = default)
    {
        await _operations.WaitAsync(token);
        try { await _process.StopAsync(token); await ReleaseLeasesAsync(); await StartCoreAsync(configuration, token); }
        finally { _operations.Release(); }
    }
    public async Task<EngineStatus> GetStatusAsync(CancellationToken token = default)
    {
        await _operations.WaitAsync(token);
        try
        {
            var state = await _process.GetStatusAsync(token);
            if (state.ProcessId is null && state.State is EngineState.Disconnected or EngineState.Error) await ReleaseLeasesAsync();
            return _preflightFailure ?? state;
        }
        finally { _operations.Release(); }
    }
    private async Task ReleaseLeasesAsync()
    {
        if (_dataLease is not null) { await _dataLease.DisposeAsync(); _dataLease = null; }
        if (_engineLease is not null) { await _engineLease.DisposeAsync(); _engineLease = null; }
    }
    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            await _process.DisposeAsync(); await ReleaseLeasesAsync(); _disposed = true;
        }
        finally { _operations.Release(); }
    }
}
