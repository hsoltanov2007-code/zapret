using System.Diagnostics;
using System.Runtime.InteropServices;
using Northpass.Models;

namespace Northpass.Engine.Zapret2;

public sealed class Zapret2Engine : IDpiEngine
{
    public static EngineDescriptor Metadata { get; } = new("zapret2", "Zapret2", "winws2.exe");
    private readonly ProcessSupervisor _process = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _disposed;
    public EngineDescriptor Descriptor => Metadata;
    private event Action<string>? _probeLog;
    public event Action<string>? LogReceived
    {
        add { _process.LogReceived += value; _probeLog += value; }
        remove { _process.LogReceived -= value; _probeLog -= value; }
    }
    public event Action<EngineStatus>? StatusChanged { add => _process.StatusChanged += value; remove => _process.StatusChanged -= value; }
    public Task<ValidationResult> ValidateConfigurationAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Zapret2ConfigurationValidator.Validate(configuration));
    }
    private async Task<ProcessStartInfo> PrepareAsync(EngineConfiguration configuration, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Zapret2 requires Windows x64 and an elevated Northpass process.");
        var result = await ValidateConfigurationAsync(configuration, token);
        if (!result.IsValid) throw new InvalidOperationException(result.Summary);
        string path = Path.GetFullPath(configuration.ExecutablePath);
        string engineDir = Path.GetDirectoryName(path)!;
        string profileDir = Path.GetDirectoryName(Path.GetFullPath(configuration.Profile.SourcePath))!;
        var info = new ProcessStartInfo(path) { WorkingDirectory = engineDir };
        foreach (string argument in configuration.Profile.Arguments)
            info.ArgumentList.Add(Zapret2ConfigurationValidator.Expand(argument, engineDir, profileDir));
        return info;
    }
    public async Task StartAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((await _process.GetStatusAsync(cancellationToken)).State == EngineState.Active)
                throw new InvalidOperationException("The engine is already running.");
            var info = await PrepareAsync(configuration, cancellationToken);
            await ProbeAsync(info, ["--version"], cancellationToken);
            await ProbeAsync(info, info.ArgumentList.Concat(new[] { "--dry-run" }).ToArray(), cancellationToken);
            await _process.StartAsync(info, cancellationToken);
        }
        finally { _operations.Release(); }
    }
    // Probes use documented flags from the reviewed version and finish before interception.
    // --dry-run checks the actual binary's parser; it does not validate Lua behavior.
    private async Task ProbeAsync(ProcessStartInfo template, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(template.FileName) { WorkingDirectory = template.WorkingDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Could not run engine preflight.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        string text = (await output + "\n" + await error).Trim();
        if (text.Length > 0) _probeLog?.Invoke("[preflight] " + text);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Engine preflight failed (code {process.ExitCode}). {text}");
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try { await _process.StopAsync(cancellationToken); }
        finally { _operations.Release(); }
    }
    public async Task RestartAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var info = await PrepareAsync(configuration, cancellationToken);
            await _process.StopAsync(cancellationToken);
            await ProbeAsync(info, info.ArgumentList.Concat(new[] { "--dry-run" }).ToArray(), cancellationToken);
            await _process.StartAsync(info, cancellationToken);
        }
        finally { _operations.Release(); }
    }
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => _process.GetStatusAsync(cancellationToken);
    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try { if (_disposed) return; await _process.DisposeAsync(); _disposed = true; }
        finally { _operations.Release(); }
    }
    public static string? Discover(string applicationDirectory)
    {
        // Discovery is deliberately local, never PATH-based or an automatic download.
        string candidate = Path.Combine(applicationDirectory, "engine", Metadata.ExecutableName);
        return File.Exists(candidate) ? candidate : null;
    }
}
