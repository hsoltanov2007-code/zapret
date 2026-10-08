using System.Diagnostics;
using System.Collections.Concurrent;
using Northpass.Models;

namespace Northpass.Engine;

// Owns exactly one foreground child. All lifecycle transitions are serialized.
// Active means the process is alive, never that a site is accessible.
public sealed class ProcessSupervisor : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private volatile EngineStatus _status = new(EngineState.Disconnected);
    private bool _disposed;
    public event Action<string>? LogReceived;
    public event Action<EngineStatus>? StatusChanged;

    private void SetStatus(EngineStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(status);
    }

    public async Task StartAsync(ProcessStartInfo info, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await StartCoreAsync(info, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync(ProcessStartInfo info, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is { HasExited: false }) throw new InvalidOperationException("The engine is already running.");
        _process?.Dispose();
        _process = null;
        SetStatus(new(EngineState.Connecting));
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var process = new Process { StartInfo = info };
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("The engine process did not start.");
            _process = process;
            SetStatus(new(EngineState.Connecting, process.Id));
            var recent = new ConcurrentQueue<string>();
            var output = PumpAsync(process.StandardOutput, "", recent);
            var error = PumpAsync(process.StandardError, "[stderr] ", recent);
            _ = ObserveExitAsync(process, output, error, recent);
            // Catch immediate argument/dependency failures, without claiming interception is verified.
            await Task.Delay(300, token);
            if (process.HasExited)
            {
                await Task.WhenAll(output, error);
                throw new InvalidOperationException($"Engine exited during startup (code {process.ExitCode}). " + string.Join("\n", recent));
            }
            SetStatus(new(EngineState.Active, process.Id, DateTimeOffset.UtcNow));
            LogReceived?.Invoke($"Engine process started (PID {process.Id}). Website reachability is unverified.");
        }
        catch (Exception ex)
        {
            if (_process == process)
            {
                // Keep ownership until cleanup succeeds, even on cancelled startup.
                try { await TerminateAsync(process); }
                catch (Exception cleanup)
                {
                    SetStatus(new(EngineState.Error, process.Id, Error: "Engine startup failed and cleanup needs retry: " + cleanup.Message));
                    throw;
                }
                _process = null;
            }
            process.Dispose();
            SetStatus(new(EngineState.Error, Error: ex.Message));
            throw;
        }
    }

    private async Task PumpAsync(StreamReader reader, string prefix, ConcurrentQueue<string> recent)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                string text = prefix + (line.Length > 16384 ? line[..16384] + "…" : line);
                recent.Enqueue(text); while (recent.Count > 8) recent.TryDequeue(out _);
                LogReceived?.Invoke(text);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { /* Owned process has closed its pipes. */ }
    }

    private async Task ObserveExitAsync(Process process, Task output, Task error, ConcurrentQueue<string> recent)
    {
        try
        {
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            await _gate.WaitAsync();
            try
            {
                if (_process != process) return;
                int code = process.ExitCode;
                _process = null;
                process.Dispose();
                LogReceived?.Invoke($"Engine exited unexpectedly (code {code}).");
                SetStatus(new(EngineState.Error, ExitCode: code, Error: $"Engine exited (code {code}). " + string.Join("\n", recent)));
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        { /* Stop or replacement already disposed this process. */ }
    }

    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("Engine did not stop within five seconds; ownership is retained. Try stopping again."); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        if (_process is not { } process) { SetStatus(new(EngineState.Disconnected)); return; }
        SetStatus(_status with { State = EngineState.Stopping });
        try
        {
            await TerminateAsync(process);
            _process = null;
            process.Dispose();
            SetStatus(new(EngineState.Disconnected));
            LogReceived?.Invoke("Owned engine process stopped.");
        }
        catch (Exception ex) { SetStatus(_status with { State = EngineState.Error, Error = ex.Message }); throw; }
    }

    public async Task RestartAsync(ProcessStartInfo info, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync();
            await StartCoreAsync(info, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: true } process && _status.State == EngineState.Active)
                SetStatus(_status with { State = EngineState.Error, ExitCode = process.ExitCode, Error = "Engine process is no longer running." });
            return _status;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            await StopCoreAsync();
            _disposed = true;
        }
        finally { _gate.Release(); }
    }
}
