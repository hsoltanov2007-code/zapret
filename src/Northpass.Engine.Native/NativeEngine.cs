using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Northpass.Models;
namespace Northpass.Engine.Native;

// The UI/controller depend on IDpiEngine, not C++/driver details. v0.2 never rewrites packets.
public sealed class NativeEngine : IDpiEngine, IEnginePerformanceProvider
{
    public static EngineDescriptor Metadata { get; } = new("native", "NorthpassCore 0.3 (experimental pass-through)", "NorthpassCore.exe");
    private readonly IEngineInstallationManager _installation;
    private readonly IEngineCollisionDetector _collisions;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ProcessSupervisor _process;
    private readonly NativePipeClient? _pipe;
    private string? _pipeId;
    private EnginePerformance? _performance;
    private IAsyncDisposable? _lease;
    private EngineStatus? _failure;
    private bool _disposed;
    public EngineDescriptor Descriptor => Metadata;
    public event Action<string>? LogReceived;
    public event Action<EngineStatus>? StatusChanged;
    public NativeEngine(IEngineInstallationManager installation, IEngineCollisionDetector? collisions = null, bool useNamedPipe = false)
    {
        if (installation.EngineId != "native") throw new ArgumentException("A native installation manager is required.");
        (_installation, _collisions) = (installation, collisions ?? new EngineCollisionDetector());
        if (useNamedPipe) _pipe = new NativePipeClient();
        _process = new(new("NORTHPASS_READY protocol=1", "STOP", TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5)),
            _pipe is null ? null : (child, token) => _pipe.ConnectAsync(child, _pipeId!, token),
            _pipe is null ? null : async token => { await _pipe.RequestAsync("STOP", token); });
        _process.LogReceived += line => {
            if (line.StartsWith("NORTHPASS_METRICS ", StringComparison.Ordinal))
            {
                try { Volatile.Write(ref _performance, NativeMetrics.Parse(line)); }
                catch (InvalidDataException) { LogReceived?.Invoke("Native metrics frame rejected."); return; }
            }
            LogReceived?.Invoke(line);
        };
        _process.StatusChanged += state => { _failure = null; StatusChanged?.Invoke(state); };
    }
    public Task<ValidationResult> ValidateConfigurationAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(NativeCatalog.Validate(configuration)); }
    public static ProcessStartInfo CreateStartInfo(EngineConfiguration configuration, string? pipeId = null)
    {
        var validation = NativeCatalog.Validate(configuration);
        if (!validation.IsValid) throw new InvalidDataException(validation.Summary);
        var info = Template(configuration.ExecutablePath);
        var p = configuration.Profile;
        info.ArgumentList.Add("--mode"); info.ArgumentList.Add(p.StrategyId == "passthrough-idle" ? "idle" : "loopback");
        if (p.StrategyId == "passthrough-loopback")
        {
            info.ArgumentList.Add("--port"); info.ArgumentList.Add(p.Native!.LoopbackPort!.Value.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--protocol"); info.ArgumentList.Add(p.Native.Transport);
        }
        info.ArgumentList.Add("--parent-pid"); info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (pipeId is null) info.ArgumentList.Add("--stdio-control");
        else
        {
            if (!NativePipeClient.ValidIdentifier(pipeId)) throw new InvalidDataException("Invalid native IPC identifier.");
            info.ArgumentList.Add("--pipe-id"); info.ArgumentList.Add(pipeId);
        }
        return info;
    }
    private static ProcessStartInfo Template(string path)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(path)) { WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!, UseShellExecute = false, CreateNoWindow = true };
        EngineProcessEnvironment.Harden(info); return info;
    }
    public static async Task VerifyInstalledVersionAsync(InstalledEngine installed, CancellationToken token)
    {
        if (installed.EngineId != "native" || installed.Version != "0.3.0") throw new InvalidDataException("Unsupported native build.");
        var text = await ProbeAsync(Template(installed.ExecutablePath), ["--version"], token);
        if (text != "NorthpassCore 0.3.0 protocol=1") throw new InvalidDataException("Native version/protocol mismatch: " + text);
    }
    private static async Task<string> ProbeAsync(ProcessStartInfo template, IEnumerable<string> args, CancellationToken token)
    {
        var info = Template(template.FileName); info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Native preflight did not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        var text = (await output + "\n" + await error).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Native preflight failed (code {process.ExitCode}): {text}");
        return text;
    }
    public async Task StartAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try { await StartCoreAsync(configuration, cancellationToken); }
        finally { _operations.Release(); }
    }
    private async Task StartCoreAsync(EngineConfiguration configuration, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((await _process.GetStatusAsync(token)).ProcessId is not null) throw new InvalidOperationException("Disconnect the owned native session before starting another.");
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64) throw new PlatformNotSupportedException("Native packet interception requires Windows 10/11 x64 with administrator privileges.");
            if (_pipe is not null) { await _pipe.DisposeAsync(); _pipeId = NativePipeClient.NewIdentifier(); }
            Volatile.Write(ref _performance, null);
            var info = CreateStartInfo(configuration, _pipeId);
            _collisions.Check(); await ReleaseLeaseAsync();
            _lease = await _installation.AcquireLaunchLeaseAsync(configuration.ExecutablePath, token);
            await VerifyInstalledVersionAsync(new("native", "", "0.3.0", configuration.ExecutablePath), token);
            var check = await ProbeAsync(info, info.ArgumentList.Append("--check"), token);
            if (!check.StartsWith("NORTHPASS_CHECK protocol=1 mode=", StringComparison.Ordinal)) throw new InvalidDataException("Native preflight handshake mismatch.");
            LogReceived?.Invoke("Native v0.2: original packet pass-through only. DPI bypass and service availability are unverified.");
            _collisions.Check(); _failure = null;
            await _process.StartAsync(info, token);
        }
        catch (Exception ex)
        {
            var state = await _process.GetStatusAsync();
            if (state.ProcessId is null) { await ReleaseLeaseAsync(); _failure = new(EngineState.Error, Error: ex.Message); }
            StatusChanged?.Invoke(_failure ?? state); throw;
        }
    }
    public async Task<EnginePerformance?> GetPerformanceAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if ((await _process.GetStatusAsync(cancellationToken)).ProcessId is null) return Volatile.Read(ref _performance);
            if (_pipe is not null) Volatile.Write(ref _performance, NativeMetrics.Parse(await _pipe.RequestAsync("METRICS", cancellationToken)));
            return Volatile.Read(ref _performance);
        }
        finally { _operations.Release(); }
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try { await _process.StopAsync(cancellationToken); _failure = null; }
        finally
        {
            try { if ((await _process.GetStatusAsync()).ProcessId is null) await ReleaseLeaseAsync(); }
            finally { _operations.Release(); }
        }
    }
    public async Task RestartAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            try { await _process.StopAsync(cancellationToken); }
            finally { if ((await _process.GetStatusAsync()).ProcessId is null) await ReleaseLeaseAsync(); }
            await StartCoreAsync(configuration, cancellationToken);
        }
        finally { _operations.Release(); }
    }
    public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try {
            var state = await _process.GetStatusAsync(cancellationToken);
            if (state.ProcessId is null) await ReleaseLeaseAsync();
            return _failure ?? state;
        } finally { _operations.Release(); }
    }
    private async Task ReleaseLeaseAsync() {
        if (_pipe is not null) await _pipe.DisposeAsync();
        if (_lease is not null) { await _lease.DisposeAsync(); _lease = null; }
    }
    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            try { await _process.DisposeAsync(); _disposed = true; }
            finally { if ((await _process.GetStatusAsync()).ProcessId is null) await ReleaseLeaseAsync(); }
        }
        finally { _operations.Release(); }
    }
}
