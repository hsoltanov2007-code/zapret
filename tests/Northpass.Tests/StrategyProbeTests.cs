using Northpass.Engine;
using Northpass.Models;
using Northpass.Services;

namespace Northpass.Tests;

public sealed class StrategyProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "northpass-probes-" + Guid.NewGuid().ToString("N"));
    private sealed class FixtureEngine : IDpiEngine
    {
        public EngineStatus Status = new(EngineState.Disconnected);
        public int Starts; public bool Invalid;
        public EngineDescriptor Descriptor => new("fixture", "Fixture only", "not-a-real-engine.exe");
        public event Action<string>? LogReceived; public event Action<EngineStatus>? StatusChanged;
        public Task StartAsync(EngineConfiguration configuration, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Starts++; Status = new(EngineState.Active, 123, DateTimeOffset.UtcNow); StatusChanged?.Invoke(Status); LogReceived?.Invoke("fixture engine only"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token = default)
        { Status = new(EngineState.Disconnected); StatusChanged?.Invoke(Status); return Task.CompletedTask; }
        public async Task RestartAsync(EngineConfiguration config, CancellationToken token = default) { await StopAsync(token); await StartAsync(config, token); }
        public Task<EngineStatus> GetStatusAsync(CancellationToken token = default) => Task.FromResult(Status);
        public Task<ValidationResult> ValidateConfigurationAsync(EngineConfiguration config, CancellationToken token = default)
            => Task.FromResult(Invalid ? new ValidationResult([new("fixture", "bad reviewed inputs")]) : ValidationResult.Valid);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FixtureTransport(bool fail = false, bool cancel = false) : IServiceProbeTransport
    {
        public int Requests;
        public Task<ServiceProbeResult> ProbeAsync(ServiceProbeTarget target, CancellationToken token)
        {
            Requests++; if (cancel) throw new OperationCanceledException("fixture cancellation", token);
            var dns = new ProbeObservation(ProbeState.Passed, "fixture DNS");
            var tcp = new ProbeObservation(fail ? ProbeState.Failed : ProbeState.Passed, "fixture TCP");
            var unknown = new ProbeObservation(ProbeState.Unknown, "not tested");
            return Task.FromResult(new ServiceProbeResult(target.Id, target.Name, target.HttpsUrl.Host, ["203.0.113.1"], fail ? null : "203.0.113.1",
                dns, tcp, fail ? unknown : dns, fail ? unknown : dns, unknown, unknown, unknown, unknown, unknown, unknown));
        }
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ProcessStateAndWebsiteStagesRemainSeparateAndVoiceIsNeverInferred(bool fail)
    {
        FixtureEngine? current = null; var registry = new EngineRegistry();
        registry.Register(new("fixture", "Fixture only", "fixture.exe"), () => current = new());
        await using var controller = new EngineController(registry);
        var profile = new StrategyProfile { Id = "fixture", Name = "fixture", Engine = "fixture", StrategyId = "general", Arguments = ["fixture"] };
        await controller.ConnectAsync(new("fixture.exe", profile), false); var old = current!;
        var transport = new FixtureTransport(fail); var records = new StrategyTestRecordStore(_root);
        var runner = new StrategyTestRunner(controller, new(transport), records);
        var record = await runner.RunAsync(profile, "Fixture provider", async (snapshot, token) =>
        {
            Assert.Equal(EngineState.Disconnected, (await controller.GetStatusAsync(token)).State);
            Assert.Equal(EngineState.Disconnected, old.Status.State);
            profile.Arguments.Clear(); Assert.Single(snapshot.Arguments);
            return (new EngineConfiguration("fixture.exe", snapshot), new string('a', 40));
        });
        Assert.Equal(EngineState.Active, record.EngineBeforeProbes.State);
        Assert.Equal(EngineState.Active, record.EngineAfterProbes.State);
        Assert.Equal(EngineState.Disconnected, (await controller.GetStatusAsync()).State);
        Assert.Equal(2, transport.Requests); Assert.Single(Directory.GetFiles(_root, "*.json"));
        Assert.Equal("Fixture provider", record.Provider); Assert.Single(record.Inputs!.Arguments); Assert.Single(record.Logs);
        foreach (var service in record.Services)
        {
            Assert.Equal(ProbeState.Passed, service.Dns.State);
            Assert.Equal(fail ? ProbeState.Failed : ProbeState.Passed, service.Tcp.State);
            foreach (var stage in new[] { service.Quic, service.Stun, service.MediaPlayback, service.Voice, service.Login, service.Gateway }) Assert.Equal(ProbeState.Unknown, stage.State);
        }
        Assert.Equal(ProbeState.Unknown, record.UserReportedVoice); Assert.Equal(2, old.Starts); // no strategy swap
    }
    [Fact]
    public async Task CancellationStopsTheOwnedSessionAndSavesOneFailureRecord()
    {
        var registry = new EngineRegistry(); registry.Register(new("fixture", "fixture", "fixture.exe"), () => new FixtureEngine());
        await using var controller = new EngineController(registry); var transport = new FixtureTransport(cancel: true);
        var runner = new StrategyTestRunner(controller, new(transport), new(_root));
        var profile = new StrategyProfile { Id = "fixture", Name = "fixture", Engine = "fixture", Arguments = ["fixture"] };
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(profile, "provider", (snapshot, token) => Task.FromResult((new EngineConfiguration("fixture.exe", snapshot), "revision"))));
        Assert.Equal(EngineState.Disconnected, (await controller.GetStatusAsync()).State);
        var file = Assert.Single(Directory.GetFiles(_root, "*.json")); Assert.Contains("fixture cancellation", File.ReadAllText(file));
    }
    [Fact]
    public async Task FailedValidationNeverRunsProbesAndStillDisconnects()
    {
        var registry = new EngineRegistry(); registry.Register(new("fixture", "fixture", "fixture.exe"), () => new FixtureEngine { Invalid = true });
        await using var controller = new EngineController(registry); var transport = new FixtureTransport();
        var runner = new StrategyTestRunner(controller, new(transport), new(_root));
        var profile = new StrategyProfile { Id = "fixture", Name = "fixture", Engine = "fixture", Arguments = ["fixture"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(profile, "provider", (snapshot, token) => Task.FromResult((new EngineConfiguration("fixture.exe", snapshot), "revision"))));
        Assert.Equal(0, transport.Requests); Assert.Equal(EngineState.Disconnected, (await controller.GetStatusAsync()).State);
        Assert.Single(Directory.GetFiles(_root, "*.json"));
    }
    [Fact]
    public async Task RealTransportRejectsArbitraryRemoteTargetsWithoutNetworkAccess()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new NetworkServiceProbeTransport().ProbeAsync(new("evil", "evil", new("https://evil.example")), default));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
