using Northpass.Engine;
using Northpass.Models;
using Northpass.Services;

namespace Northpass.Tests;

public sealed class EngineControllerTests
{
    private sealed class TestEngine : IDpiEngine
    {
        public EngineDescriptor Descriptor { get; } = new("test", "Test engine", "test.exe");
        public int Starts, Stops, Restarts;
        public bool FailRestart;
        public StrategyProfile? StartedProfile;
        public EngineStatus Status = new(EngineState.Disconnected);
        public event Action<string>? LogReceived;
        public event Action<EngineStatus>? StatusChanged;
        public Task StartAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Starts++; StartedProfile = configuration.Profile;
            Status = new(EngineState.Active, 123, DateTimeOffset.UtcNow);
            StatusChanged?.Invoke(Status); LogReceived?.Invoke("test start"); return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        { Stops++; Status = new(EngineState.Disconnected); StatusChanged?.Invoke(Status); return Task.CompletedTask; }
        public async Task RestartAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Restarts++; await StopAsync(cancellationToken);
            if (FailRestart) { Crash(); throw new IOException("test crash"); }
            await StartAsync(configuration, cancellationToken);
        }
        public void Crash() { Status = new(EngineState.Error, ExitCode: 17, Error: "test crash"); StatusChanged?.Invoke(Status); }
        public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);
        public Task<ValidationResult> ValidateConfigurationAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default) => Task.FromResult(ValidationResult.Valid);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static (EngineController Controller, Func<TestEngine> Engine) Controller()
    {
        TestEngine? current = null;
        var registry = new EngineRegistry();
        registry.Register(new("test", "Test engine", "test.exe"), () => current = new());
        return (new(registry), () => current!);
    }
    private static EngineConfiguration Config()
    {
        var profile = ProfileTests.Profile(); profile.Engine = "test"; return new("test.exe", profile);
    }
    [Fact]
    public async Task ReplacementEngineUsesSameControllerAndSnapshotIsImmutable()
    {
        var (controller, engine) = Controller(); await using var owned = controller;
        var config = Config();
        await controller.ConnectAsync(config, false);
        Assert.Equal(EngineState.Active, (await controller.GetStatusAsync()).State);
        config.Profile.Arguments.Clear();
        Assert.Single(engine().StartedProfile!.Arguments);
        await controller.DisconnectAsync(); Assert.Equal(EngineState.Disconnected, (await controller.GetStatusAsync()).State);
    }
    [Fact]
    public async Task RepeatedConnectDoesNotLaunchAdditionalEngine()
    {
        var (controller, engine) = Controller(); await using var owned = controller;
        await controller.ConnectAsync(Config(), false);
        var active = engine();
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConnectAsync(Config(), false));
        Assert.Equal(1, active.Starts); Assert.Equal(0, active.Stops);
    }
    [Fact]
    public async Task UnknownEngineDoesNotStopWorkingEngine()
    {
        var (controller, engine) = Controller(); await using var owned = controller;
        await controller.ConnectAsync(Config(), false); var active = engine();
        var bad = Config(); bad.Profile.Engine = "native";
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConnectAsync(bad, false));
        Assert.Equal(0, active.Stops); Assert.Equal(EngineState.Active, (await controller.GetStatusAsync()).State);
    }
    [Fact]
    public async Task DisconnectCancelsPendingRecovery()
    {
        var (controller, engine) = Controller(); await using var owned = controller;
        await controller.ConnectAsync(Config(), true); var active = engine(); active.Crash();
        await controller.DisconnectAsync(); await Task.Delay(2300);
        Assert.Equal(0, active.Restarts);
    }
    [Fact]
    public async Task RecoveryNeverExceedsThreeAttempts()
    {
        var (controller, engine) = Controller(); await using var owned = controller;
        await controller.ConnectAsync(Config(), true); var active = engine(); active.FailRestart = true;
        var limit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.LogReceived += message => { if (message.StartsWith("Recovery limit reached")) limit.TrySetResult(); };
        active.Crash(); await limit.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(3, active.Restarts); Assert.Equal(EngineState.Error, (await controller.GetStatusAsync()).State);
    }
    [Fact]
    public async Task DisposalIsIdempotent()
    {
        var (controller, _) = Controller();
        await controller.ConnectAsync(Config(), false); await controller.DisposeAsync(); await controller.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => controller.ConnectAsync(Config(), false));
    }
}
