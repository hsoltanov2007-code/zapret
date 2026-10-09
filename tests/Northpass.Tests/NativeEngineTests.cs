using System.Collections.Concurrent;
using Northpass.Engine;
using Northpass.Engine.Native;
using Northpass.Models;
namespace Northpass.Tests;

public sealed class NativeEngineTests
{
    private static string Executable => Path.GetFullPath("NorthpassCore.exe");
    private static ChildProcessProtocol Protocol(TimeSpan? deadline = null) => new("NORTHPASS_READY protocol=1", "STOP", deadline ?? TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
    public const string MetricsLine = "NORTHPASS_METRICS protocol=2 cpu_percent=1.250 memory_bytes=1234 packets_per_second=40.000 latency_us=100.000 max_latency_us=200.000 queue_size=1 queue_capacity=8 queue_peak=4 captured=40 forwarded=39 dropped_known=1 kernel_loss_unknown=1 backpressure=2 active_flows=3 recoverable_errors=1 fatal_errors=1 auth_failures=0";
    [Fact]
    public void MetricsAreTypedBoundedAndDoNotClaimLosslessDelivery()
    {
        var metrics = NativeMetrics.Parse(MetricsLine);
        Assert.True(metrics.KernelLossUnknown); Assert.Equal(1ul, metrics.KnownDropped); Assert.Equal(1.25, metrics.CpuPercent);
        Assert.Equal(8ul, metrics.QueueCapacity); Assert.Equal(3ul, metrics.ActiveFlows);
    }
    [Theory]
    [InlineData("cpu_percent=1.250", "cpu_percent=NaN")]
    [InlineData("cpu_percent=1.250", "cpu_percent=101")]
    [InlineData("queue_size=1", "queue_size=9")]
    [InlineData("active_flows=3", "active_flows=4097")]
    [InlineData("kernel_loss_unknown=1", "kernel_loss_unknown=0")]
    [InlineData("protocol=2", "protocol=1")]
    [InlineData("memory_bytes=1234", "memory_bytes=-1")]
    [InlineData("memory_bytes=1234", "cpu_percent=5")]
    public void UntrustedMetricsCannotChangeTheSchemaOrExceedLimits(string field, string replacement)
        => Assert.Throws<InvalidDataException>(() => NativeMetrics.Parse(MetricsLine.Replace(field, replacement)));
    [Fact]
    public void PipeIdentifiersCannotInjectPathsAndContainNoAuthenticationToken()
    {
        var id = NativePipeClient.NewIdentifier(); Assert.True(NativePipeClient.ValidIdentifier(id));
        var info = NativeEngine.CreateStartInfo(new(Executable, NativeCatalog.Idle()), id);
        Assert.DoesNotContain("--stdio-control", info.ArgumentList); Assert.Equal(id, info.ArgumentList.Last());
        Assert.Throws<InvalidDataException>(() => NativeEngine.CreateStartInfo(new(Executable, NativeCatalog.Idle()), "../pipe"));
    }
    [Fact]
    public async Task OwnedInitializationFailureAndCancellationCleanUpTheChild()
    {
        await using var failed = new ProcessSupervisor(Protocol(), (_, _) => throw new UnauthorizedAccessException("Fixture authentication rejected."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => failed.StartAsync(ProcessSupervisorTests.Child("protocol")));
        Assert.Null((await failed.GetStatusAsync()).ProcessId);
        await using var cancelled = new ProcessSupervisor(Protocol(), (_, token) => Task.Delay(5000, token));
        using var stop = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.StartAsync(ProcessSupervisorTests.Child("protocol"), stop.Token));
        Assert.Null((await cancelled.GetStatusAsync()).ProcessId);
    }
    [Fact]
    public void TypedScopeCompilesOnlyExplicitArgumentsAndSurvivesControllerCopy()
    {
        var p = NativeCatalog.Loopback(52000, "tcp"); var copy = ProfileValidation.Copy(p);
        p.Native = new(53000, "udp");
        var configured = new EngineConfiguration(Executable, copy);
        Assert.True(ProfileValidation.Validate(copy).IsValid); Assert.True(NativeCatalog.Validate(configured).IsValid);
        var info = NativeEngine.CreateStartInfo(configured);
        Assert.Equal(new[] { "--mode", "loopback", "--port", "52000", "--protocol", "tcp", "--parent-pid", Environment.ProcessId.ToString(), "--stdio-control" }, info.ArgumentList);
        Assert.False(info.UseShellExecute); Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.System), info.Environment["PATH"]);
        Assert.DoesNotContain(info.ArgumentList, value => value.Contains("filter"));
        Assert.True(NativeCatalog.Validate(new(Executable, NativeCatalog.Idle())).IsValid);
    }
    [Theory]
    [InlineData(443, "tcp")]
    [InlineData(65536, "udp")]
    [InlineData(52000, "true")]
    [InlineData(52000, "tcp and true")]
    public void InvalidScopesCannotReachTheChild(int port, string transport)
        => Assert.Throws<InvalidDataException>(() => NativeEngine.CreateStartInfo(new(Executable, NativeCatalog.Loopback(port, transport))));
    [Fact]
    public void RawArgumentsListsDifferentEnginesAndUnknownStrategiesAreRejected()
    {
        var p = NativeCatalog.Idle(); p.Arguments.Add("--filter=true"); Assert.False(NativeCatalog.Validate(new(Executable, p)).IsValid);
        p.Arguments.Clear(); p.ListBindings.Add("module", "untrusted.dll"); Assert.False(NativeCatalog.Validate(new(Executable, p)).IsValid);
        p.ListBindings.Clear(); p.Engine = "zapret1"; Assert.False(NativeCatalog.Validate(new(Executable, p)).IsValid);
        p.Engine = "native"; p.StrategyId = "bypass"; Assert.False(NativeCatalog.Validate(new(Executable, p)).IsValid);
        Assert.False(NativeCatalog.Validate(new("NorthpassCore.exe", NativeCatalog.Idle())).IsValid);
    }
    [Fact]
    public async Task ActualHandshakeAndStopProtocolAreRequired()
    {
        var logs = new ConcurrentQueue<string>();
        await using var supervisor = new ProcessSupervisor(Protocol()); supervisor.LogReceived += logs.Enqueue;
        await supervisor.StartAsync(ProcessSupervisorTests.Child("protocol"));
        Assert.Equal(EngineState.Active, (await supervisor.GetStatusAsync()).State);
        await supervisor.StopAsync();
        Assert.Contains("fixture graceful stop", logs);
        Assert.DoesNotContain(logs, text => text.Contains("Graceful native shutdown failed"));
        Assert.Null((await supervisor.GetStatusAsync()).ProcessId);
    }
    [Theory]
    [InlineData("protocol-stderr")]
    [InlineData("protocol-hang")]
    public async Task UnconfirmedStartupTimesOutAndOwnedProcessIsCleaned(string mode)
    {
        await using var supervisor = new ProcessSupervisor(Protocol(TimeSpan.FromMilliseconds(500)));
        await Assert.ThrowsAsync<TimeoutException>(() => supervisor.StartAsync(ProcessSupervisorTests.Child(mode)));
        Assert.Null((await supervisor.GetStatusAsync()).ProcessId);
    }
    [Fact]
    public async Task CancelledNativeHandshakeAndImmediateExitCleanUpOwnership()
    {
        await using var supervisor = new ProcessSupervisor(Protocol());
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor.StartAsync(ProcessSupervisorTests.Child("protocol-hang"), cancel.Token));
        Assert.Null((await supervisor.GetStatusAsync()).ProcessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartAsync(ProcessSupervisorTests.Child("exit")));
        await supervisor.StartAsync(ProcessSupervisorTests.Child("protocol"));
        await supervisor.StopAsync();
    }
    [Fact]
    public async Task NonCooperativeChildHasBoundedOwnedTermination()
    {
        await using var supervisor = new ProcessSupervisor(new("fixture ready", "STOP", TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100)));
        var logs = new ConcurrentQueue<string>(); supervisor.LogReceived += logs.Enqueue;
        await supervisor.StartAsync(ProcessSupervisorTests.Child());
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StopAsync());
        Assert.Contains(logs, line => line.StartsWith("Graceful native shutdown failed"));
        Assert.Null((await supervisor.GetStatusAsync()).ProcessId);
    }
    [Fact]
    public async Task NativeShutdownErrorIsReportedAfterOwnedCleanupAndRestartRemainsPossible()
    {
        await using var supervisor = new ProcessSupervisor(Protocol());
        await supervisor.StartAsync(ProcessSupervisorTests.Child("protocol-error"));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StopAsync());
        Assert.Contains("code 27", failure.Message);
        var state = await supervisor.GetStatusAsync(); Assert.Equal(27, state.ExitCode); Assert.Equal(EngineState.Error, state.State); Assert.Null(state.ProcessId);
        await supervisor.StartAsync(ProcessSupervisorTests.Child("protocol")); await supervisor.StopAsync();
    }
}
