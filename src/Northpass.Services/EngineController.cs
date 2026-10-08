using Northpass.Engine;
using Northpass.Models;

namespace Northpass.Services;

public sealed class EngineController(EngineRegistry registry) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDpiEngine? _engine;
    private EngineConfiguration? _configuration;
    private CancellationTokenSource _recovery = new();
    private bool _desiredRunning, _recovering, _allowRecovery, _disposed;
    private int _attempts;
    public const int MaximumRecoveryAttempts = 3;
    public event Action<string>? LogReceived;
    public event Action<EngineStatus>? StatusChanged;
    private void Log(string message) => LogReceived?.Invoke(message);
    private void OnStatus(EngineStatus status)
    {
        StatusChanged?.Invoke(status);
        if (status.State == EngineState.Error && _desiredRunning && _allowRecovery && !_recovering)
        {
            _recovering = true;
            _ = RecoverAsync(_recovery.Token);
        }
    }
    private async Task SelectEngineAsync(string id)
    {
        if (string.Equals(_engine?.Descriptor.Id, id, StringComparison.OrdinalIgnoreCase)) return;
        var next = registry.Create(id);
        try
        {
            if (_engine is not null)
            {
                await _engine.StopAsync();
                await _engine.DisposeAsync();
                _engine.LogReceived -= Log;
                _engine.StatusChanged -= OnStatus;
            }
        }
        catch { await next.DisposeAsync(); throw; }
        _engine = next;
        _engine.LogReceived += Log;
        _engine.StatusChanged += OnStatus;
    }
    public async Task<ValidationResult> ValidateAsync(EngineConfiguration configuration, CancellationToken token = default)
    {
        var structural = ProfileValidation.Validate(configuration.Profile);
        if (!structural.IsValid) return structural;
        if (registry.Find(configuration.Profile.Engine) is null)
            return new([new("engine.unknown", "Engine is not installed: " + configuration.Profile.Engine)]);
        await using var engine = registry.Create(configuration.Profile.Engine);
        return await engine.ValidateConfigurationAsync(configuration, token);
    }
    public async Task ConnectAsync(EngineConfiguration configuration, bool autoRecover, CancellationToken token = default)
    {
        var validation = await ValidateAsync(configuration, token);
        if (!validation.IsValid) throw new InvalidOperationException(validation.Summary);
        var snapshot = new EngineConfiguration(configuration.ExecutablePath, ProfileValidation.Copy(configuration.Profile));
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_engine is not null && (await _engine.GetStatusAsync(token)).State is EngineState.Active or EngineState.Connecting or EngineState.Stopping)
                throw new InvalidOperationException("Disconnect the current session before selecting another strategy.");
            CancelRecovery();
            _desiredRunning = false;
            await SelectEngineAsync(snapshot.Profile.Engine);
            var state = await _engine!.GetStatusAsync(token);
            if (state.State is EngineState.Active or EngineState.Connecting or EngineState.Stopping)
                throw new InvalidOperationException("Disconnect the current session before selecting another strategy.");
            _configuration = snapshot;
            _allowRecovery = autoRecover;
            _attempts = 0;
            await _engine.StartAsync(snapshot, token);
            _desiredRunning = true;
            OnStatus(await _engine.GetStatusAsync(token));
        }
        finally { _gate.Release(); }
    }
    private void CancelRecovery()
    {
        _recovery.Cancel();
        _recovery.Dispose();
        _recovery = new();
    }
    public async Task DisconnectAsync(CancellationToken token = default)
    {
        _desiredRunning = false;
        _recovery.Cancel();
        await _gate.WaitAsync(token);
        try { if (_engine is not null) await _engine.StopAsync(token); }
        finally { _gate.Release(); }
    }
    private async Task RecoverAsync(CancellationToken token)
    {
        try
        {
            while (_attempts < MaximumRecoveryAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                await _gate.WaitAsync(token);
                try
                {
                    if (!_desiredRunning || _disposed || _engine is null || _configuration is null) return;
                    _attempts++;
                    Log($"Recovery attempt {_attempts}/{MaximumRecoveryAttempts}.");
                    try
                    {
                        await _engine.RestartAsync(_configuration, token);
                        if ((await _engine.GetStatusAsync(token)).State == EngineState.Active) return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log("Recovery failed: " + ex.Message); }
                }
                finally { _gate.Release(); }
            }
            _desiredRunning = false;
            Log("Recovery limit reached. Reconnect manually after checking diagnostics.");
        }
        catch (OperationCanceledException) { }
        finally { _recovering = false; }
    }
    public async Task<EngineStatus> GetStatusAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return _engine is null ? new(EngineState.Disconnected) : await _engine.GetStatusAsync(token); }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync();
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            if (_engine is not null)
            {
                _engine.LogReceived -= Log;
                _engine.StatusChanged -= OnStatus;
                await _engine.DisposeAsync();
            }
            _disposed = true;
            _recovery.Dispose();
        }
        finally { _gate.Release(); }
    }
}
