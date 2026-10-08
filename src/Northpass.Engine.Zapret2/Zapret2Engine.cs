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
    private readonly IEngineInstallationManager? _installation;
    private IAsyncDisposable? _launchLease;
    private readonly IEngineCollisionDetector _collisions = new EngineCollisionDetector();
    public Zapret2Engine(IEngineInstallationManager? installation = null) => _installation = installation;
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
        var validation = Zapret2ConfigurationValidator.Validate(configuration);
        if (_installation is not null && validation.IsValid)
        {
            var issues = validation.Issues.ToList();
            string engineDirectory = Path.GetDirectoryName(Path.GetFullPath(configuration.ExecutablePath))!;
            string profileDirectory = Path.GetDirectoryName(Path.GetFullPath(configuration.Profile.SourcePath))!;
            foreach (string raw in configuration.Profile.Arguments)
            {
                string argument = Zapret2ConfigurationValidator.Expand(raw, engineDirectory, profileDirectory);
                if (argument.StartsWith("--lua-init=@", StringComparison.Ordinal) && !TrustedLuaPath(argument[12..], engineDirectory))
                    issues.Add(new("engine.lua-trust", "Managed engine Lua must come from the verified protected engine installation. External Lua is not allowed."));
            }
            validation = new(issues);
        }
        return Task.FromResult(validation);
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
            _collisions.Check((await _process.GetStatusAsync(cancellationToken)).ProcessId);
            var info = await PrepareAsync(configuration, cancellationToken);
            await ReleaseLeaseAsync();
            _launchLease = _installation is null ? null : await _installation.AcquireLaunchLeaseAsync(info.FileName, cancellationToken);
            try
            {
                ValidateManagedAssets(configuration, info);
                await ProbeAsync(info, ["--version"], cancellationToken);
                await ProbeAsync(info, info.ArgumentList.Concat(new[] { "--dry-run" }).ToArray(), cancellationToken);
                await _process.StartAsync(info, cancellationToken);
            }
            catch { if ((await _process.GetStatusAsync()).ProcessId is null) await ReleaseLeaseAsync(); throw; }
        }
        finally { _operations.Release(); }
    }
    // Probes use documented flags from the reviewed version and finish before interception.
    // --dry-run checks the actual binary's parser; it does not validate Lua behavior.
    private async Task ProbeAsync(ProcessStartInfo template, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(template.FileName) { WorkingDirectory = template.WorkingDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear();
        foreach (var variable in template.Environment) info.Environment[variable.Key] = variable.Value;
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
        try { await _process.StopAsync(cancellationToken); await ReleaseLeaseAsync(); }
        finally { _operations.Release(); }
    }
    public async Task RestartAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _collisions.Check((await _process.GetStatusAsync(cancellationToken)).ProcessId);
            var info = await PrepareAsync(configuration, cancellationToken);
            await _process.StopAsync(cancellationToken);
            await ReleaseLeaseAsync();
            _launchLease = _installation is null ? null : await _installation.AcquireLaunchLeaseAsync(info.FileName, cancellationToken);
            try
            {
                ValidateManagedAssets(configuration, info);
                await ProbeAsync(info, ["--version"], cancellationToken);
                await ProbeAsync(info, info.ArgumentList.Concat(new[] { "--dry-run" }).ToArray(), cancellationToken);
                await _process.StartAsync(info, cancellationToken);
            }
            catch { if ((await _process.GetStatusAsync()).ProcessId is null) await ReleaseLeaseAsync(); throw; }
        }
        finally { _operations.Release(); }
    }
    public async Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var status = await _process.GetStatusAsync(cancellationToken);
            if (status.ProcessId is null && status.State is EngineState.Error or EngineState.Disconnected) await ReleaseLeaseAsync();
            return status;
        }
        finally { _operations.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _operations.WaitAsync();
        try { if (_disposed) return; await _process.DisposeAsync(); await ReleaseLeaseAsync(); _disposed = true; }
        finally { _operations.Release(); }
    }
    private async Task ReleaseLeaseAsync()
    {
        if (_launchLease is not null) { await _launchLease.DisposeAsync(); _launchLease = null; }
    }
    private void ValidateManagedAssets(EngineConfiguration configuration, ProcessStartInfo info)
    {
        if (_installation is null) return;
        foreach (string argument in info.ArgumentList)
        {
            if (!argument.StartsWith("--lua-init=@", StringComparison.Ordinal)) continue;
            if (!TrustedLuaPath(argument[12..], info.WorkingDirectory))
                throw new InvalidOperationException("Managed engine Lua must come from the verified protected engine installation. Imported Lua cannot run elevated.");
        }
        // Prevent environment-based DLL/Lua substitution. The executable and working directory are protected.
        EngineProcessEnvironment.Harden(info);
    }
    private static bool TrustedLuaPath(string path, string directory)
    {
        string file = Path.GetFullPath(path, directory);
        string relative = Path.GetRelativePath(directory, file);
        return !relative.StartsWith("..") && !Path.IsPathRooted(relative) && file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
    }
    public static async Task VerifyInstalledVersionAsync(InstalledEngine engine, CancellationToken token)
    {
        var info = new ProcessStartInfo(engine.ExecutablePath) { WorkingDirectory = Path.GetDirectoryName(engine.ExecutablePath)!,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("--version");
        EngineProcessEnvironment.Harden(info);
        using var process = Process.Start(info) ?? throw new IOException("Engine version probe could not start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        string text = await output + await error;
        if (process.ExitCode != 0 || !text.Contains("github version v" + engine.Version + " (" + engine.SourceRevision + ")", StringComparison.Ordinal))
            throw new InvalidDataException("Engine version does not match the reviewed manifest, or its runtime could not initialize. " + text);
    }
    public static string? Discover(string applicationDirectory)
    {
        // Discovery is deliberately local, never PATH-based or an automatic download.
        string candidate = Path.Combine(applicationDirectory, "engine", Metadata.ExecutableName);
        return File.Exists(candidate) ? candidate : null;
    }
}
