using System.Collections.Concurrent;
using System.Diagnostics;
using Northpass.Engine;
using Northpass.Models;

namespace Northpass.Tests;

public sealed class ProcessSupervisorTests
{
    public static ProcessStartInfo Child(string mode = "wait", params string[] arguments)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "fixture", "Northpass.TestChild.dll"));
        info.ArgumentList.Add(mode);
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }
    private static bool Alive(int id)
    {
        // Container PID 1 may leave killed grandchildren as zombies. They cannot
        // execute, but .NET's non-child HasExited can still report false for them.
        if (OperatingSystem.IsLinux())
        {
            try
            {
                string stat = File.ReadAllText($"/proc/{id}/stat");
                if (stat[(stat.LastIndexOf(')') + 2)] == 'Z') return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    [Fact]
    public async Task StartStopAndDoubleStartUseExactlyOneOwnedProcess()
    {
        await using var supervisor = new ProcessSupervisor();
        await supervisor.StartAsync(Child());
        var active = await supervisor.GetStatusAsync();
        Assert.Equal(EngineState.Active, active.State); Assert.True(Alive(active.ProcessId!.Value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartAsync(Child()));
        Assert.Equal(active.ProcessId, (await supervisor.GetStatusAsync()).ProcessId);
        await supervisor.StopAsync();
        Assert.False(Alive(active.ProcessId.Value)); Assert.Equal(EngineState.Disconnected, (await supervisor.GetStatusAsync()).State);
        await supervisor.StopAsync();
    }
    [Fact]
    public async Task RestartStopsOldProcessBeforeStartingNewOne()
    {
        await using var supervisor = new ProcessSupervisor();
        await supervisor.StartAsync(Child()); int old = (await supervisor.GetStatusAsync()).ProcessId!.Value;
        await supervisor.RestartAsync(Child()); int next = (await supervisor.GetStatusAsync()).ProcessId!.Value;
        Assert.NotEqual(old, next); Assert.False(Alive(old)); Assert.True(Alive(next));
    }
    [Fact]
    public async Task ArgumentsAreNotInterpretedByShellAndBothStreamsAreLogged()
    {
        var logs = new ConcurrentQueue<string>();
        await using var supervisor = new ProcessSupervisor(); supervisor.LogReceived += logs.Enqueue;
        await supervisor.StartAsync(Child("wait", "path with spaces", "& echo injected", "$(echo injected)", "quote\"inside"));
        Assert.Contains(logs, l => l.Contains("path with spaces") && l.Contains("$(echo injected)"));
        Assert.Contains(logs, l => l == "[stderr] fixture stderr");
    }
    [Fact]
    public async Task ImmediateExitIsFailureAndCanBeFollowedByHealthyStart()
    {
        await using var supervisor = new ProcessSupervisor();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartAsync(Child("exit")));
        Assert.Contains("fixture startup error", failure.Message);
        Assert.Equal(EngineState.Error, (await supervisor.GetStatusAsync()).State);
        await supervisor.StartAsync(Child()); Assert.Equal(EngineState.Active, (await supervisor.GetStatusAsync()).State);
    }
    [Fact]
    public async Task CrashReportsActualExitCode()
    {
        await using var supervisor = new ProcessSupervisor();
        string signal = Path.Combine(Path.GetTempPath(), "northpass-crash-" + Guid.NewGuid().ToString("N"));
        await supervisor.StartAsync(Child("crash", signal));
        try
        {
            await File.WriteAllTextAsync(signal, "crash after acknowledged startup");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            EngineStatus status;
            do { await Task.Delay(100); status = await supervisor.GetStatusAsync(); } while (status.State == EngineState.Active && DateTime.UtcNow < deadline);
            Assert.Equal(EngineState.Error, status.State); Assert.Equal(23, status.ExitCode);
        } finally { File.Delete(signal); }
    }
    [Fact]
    public async Task CancelledStartupDoesNotLeaveAnOwnedProcess()
    {
        await using var supervisor = new ProcessSupervisor();
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor.StartAsync(Child(), cancel.Token));
        Assert.Null((await supervisor.GetStatusAsync()).ProcessId);
        await supervisor.StartAsync(Child());
    }
    [Fact]
    public async Task ConcurrentStartsDoNotCreateDuplicates()
    {
        await using var supervisor = new ProcessSupervisor();
        async Task<bool> Start() { try { await supervisor.StartAsync(Child()); return true; } catch (InvalidOperationException) { return false; } }
        var results = await Task.WhenAll(Start(), Start(), Start());
        Assert.Single(results, r => r);
    }
    [Fact]
    public async Task DisposeStopsTheRealChildAndIsIdempotent()
    {
        var supervisor = new ProcessSupervisor(); await supervisor.StartAsync(Child());
        int id = (await supervisor.GetStatusAsync()).ProcessId!.Value;
        await supervisor.DisposeAsync(); await supervisor.DisposeAsync();
        Assert.False(Alive(id));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => supervisor.StartAsync(Child()));
    }
    [Fact]
    public async Task StopTerminatesDescendantProcess()
    {
        var childPid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ProcessSupervisor();
        supervisor.LogReceived += line => { if (line.StartsWith("child-pid=")) childPid.TrySetResult(int.Parse(line[10..])); };
        await supervisor.StartAsync(Child("spawn"));
        int id = await childPid.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Alive(id)); await supervisor.StopAsync();
        // Linux may briefly retain a reaped child entry after tree termination.
        for (int n = 0; n < 20 && Alive(id); n++) await Task.Delay(100);
        Assert.False(Alive(id));
    }
}
