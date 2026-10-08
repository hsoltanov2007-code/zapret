using System.Diagnostics;
using System.Collections.Concurrent;
using Northpass.Models;

namespace Northpass.Engine;

// Optional owned-child protocol. Legacy engines retain their existing startup behavior.
public sealed record ChildProcessProtocol(string ReadyLine, string StopCommand, TimeSpan StartupTimeout, TimeSpan ShutdownTimeout);

// Owns exactly one foreground child. All lifecycle transitions are serialized.
// Active means the process is alive, never that a site is accessible.
public sealed class ProcessSupervisor : IAsyncDisposable
{
    private readonly ChildProcessProtocol? _protocol;
    private Task _pumps = Task.CompletedTask;
    private readonly Func<Process, CancellationToken, Task>? _initializeChild;
    private readonly Func<CancellationToken, Task>? _stopChild;
    public ProcessSupervisor(ChildProcessProtocol? protocol = null,
        Func<Process, CancellationToken, Task>? initializeChild = null, Func<CancellationToken, Task>? stopChild = null)
        => (_protocol, _initializeChild, _stopChild) = (protocol, initializeChild, stopChild);
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
        info.RedirectStandardInput = _protocol is not null;
        var process = new Process { StartInfo = info };
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("The engine process did not start.");
            _process = process;
            SetStatus(new(EngineState.Connecting, process.Id));
            var recent = new ConcurrentQueue<string>();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = PumpAsync(process.StandardOutput, "", recent, ready);
            var error = PumpAsync(process.StandardError, "[stderr] ", recent, null);
            _pumps = Task.WhenAll(output, error);
            _ = ObserveExitAsync(process, output, error, recent);
            // Catch immediate argument/dependency failures, without claiming interception is verified.
            if (_protocol is null) await Task.Delay(300, token);
            else
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
                startup.CancelAfter(_protocol.StartupTimeout);
                try
                {
                    if (_initializeChild is not null) await _initializeChild(process, startup.Token);
                    var exited = process.WaitForExitAsync(startup.Token);
                    var completed = await Task.WhenAny(ready.Task, exited).WaitAsync(startup.Token);
                    if (completed == exited) await exited;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("Native engine did not confirm initialization within its startup deadline."); }
            }
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
                try { await RequestGracefulStopAsync(process); await TerminateAsync(process); await _pumps; }
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

    private async Task PumpAsync(StreamReader reader, string prefix, ConcurrentQueue<string> recent, TaskCompletionSource? ready)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                string text = prefix + (line.Length > 16384 ? line[..16384] + "…" : line);
                recent.Enqueue(text); while (recent.Count > 8) recent.TryDequeue(out _);
                LogReceived?.Invoke(text);
                if (prefix.Length == 0 && line == _protocol?.ReadyLine) ready?.TrySetResult();
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

    private async Task RequestGracefulStopAsync(Process process)
    {
        if (_protocol is null || process.HasExited) return;
        try
        {
            using var timeout = new CancellationTokenSource(_protocol.ShutdownTimeout);
            if (_stopChild is not null) await _stopChild(timeout.Token);
            else
            {
                await process.StandardInput.WriteLineAsync(_protocol.StopCommand);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { LogReceived?.Invoke("Graceful native shutdown failed; terminating only the owned child: " + ex.Message); }
    }

    private async Task StopCoreAsync()
    {
        if (_process is not { } process) { SetStatus(new(EngineState.Disconnected)); return; }
        SetStatus(_status with { State = EngineState.Stopping });
        try
        {
            await RequestGracefulStopAsync(process);
            await TerminateAsync(process);
            await _pumps;
            int exitCode = process.ExitCode;
            _process = null;
            process.Dispose();
            if (_protocol is not null && exitCode != 0)
            {
                var failure = new InvalidOperationException($"Native engine stopped with an error (code {exitCode}). See the diagnostic logs; scoped packet delivery is not guaranteed.");
                SetStatus(new(EngineState.Error, ExitCode: exitCode, Error: failure.Message));
                throw failure;
            }
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
