using System.Collections.Concurrent;
using Northpass.Engine;
using Northpass.Engine.Zapret1;
using Northpass.Models;

namespace Northpass.Tests;

public sealed class FlowsealCaptureTests
{
    [Fact]
    public void InitializationIsNotPacketMatchOrTransformationEvidenceAndLossRevokesIt()
    {
        var observer = new FlowsealCaptureObserver(); observer.Reset(FlowsealCatalog.Profile(FlowsealCatalog.Find("general")!), false);
        Assert.Equal(CaptureState.Unconfirmed, observer.Evidence.Capture);
        observer.Observe("[stderr] " + FlowsealCaptureObserver.ReadyLine); Assert.Equal(CaptureState.Unconfirmed, observer.Evidence.Capture);
        observer.Observe(FlowsealCaptureObserver.ReadyLine);
        Assert.Equal(CaptureState.Initialized, observer.Evidence.Capture);
        Assert.Null(observer.Evidence.TrafficMatched); Assert.Null(observer.Evidence.TransformationConfirmed);
        Assert.Contains("443", observer.Evidence.TcpPorts); Assert.Contains("50000-50100", observer.Evidence.UdpPorts);
        observer.Observe("logical network disappeared. deinitializing windivert."); Assert.Equal(CaptureState.Unavailable, observer.Evidence.Capture);
        observer.Observe(FlowsealCaptureObserver.ReadyLine); Assert.Equal(CaptureState.Initialized, observer.Evidence.Capture);
        observer.Observe("[stderr] windivert: reinject of packet id=1 failed"); Assert.Equal(CaptureState.Failed, observer.Evidence.Capture);
        observer.Reset(FlowsealCatalog.Profile(FlowsealCatalog.Find("alt")!), false);
        Assert.Equal(CaptureState.Unconfirmed, observer.Evidence.Capture); Assert.Equal("alt", observer.Evidence.Strategy);
    }
    [Fact]
    public void CaptureLossCannotStayActiveAndOwnedProcessIsRetainedForStopping()
    {
        var observer = new FlowsealCaptureObserver(); observer.Reset(FlowsealCatalog.Profile(FlowsealCatalog.Find("general")!), false);
        observer.Observe(FlowsealCaptureObserver.ReadyLine); var active = new EngineStatus(EngineState.Active, 123, DateTimeOffset.UtcNow);
        Assert.Equal(EngineState.Active, observer.Apply(active).State);
        observer.Observe("logical network disappeared. deinitializing windivert.");
        var lost = observer.Apply(active); Assert.Equal(EngineState.Error, lost.State); Assert.Equal(123, lost.ProcessId);
        Assert.Null(lost.Traffic!.TrafficMatched); Assert.Null(lost.Traffic.TransformationConfirmed);
        observer.Observe(FlowsealCaptureObserver.ReadyLine); Assert.Equal(EngineState.Active, observer.Apply(active).State);
        Assert.Equal(CaptureState.Unconfirmed, observer.Apply(new(EngineState.Disconnected)).Traffic!.Capture);
        Assert.Equal(CaptureState.Failed, observer.Apply(new(EngineState.Error, ExitCode: 17)).Traffic!.Capture);
    }
    [Fact]
    public void UnsafeUpstreamAdviceIsExcludedButDriverFailureRetained()
    {
        Assert.Null(FlowsealCaptureObserver.SafeLog("[stderr] windivert: try to disable secure boot and install OS patches"));
        Assert.Equal("[stderr] windivert: error opening filter: code 577", FlowsealCaptureObserver.SafeLog("[stderr] windivert: error opening filter: code 577"));
    }
    [Fact]
    public void FalseFilterIsAlwaysMarkedTestDisabledEvenWhenDriverInitializes()
    {
        var observer = new FlowsealCaptureObserver(); observer.Reset(FlowsealCatalog.Profile(FlowsealCatalog.Find("general")!), true);
        observer.Observe(FlowsealCaptureObserver.ReadyLine); Assert.Equal(CaptureState.TestDisabled, observer.Evidence.Capture);
    }
    [Fact]
    public async Task SurvivingProcessWithoutReadinessTimesOutAndCleanupPermitsReconnect()
    {
        var pids = new ConcurrentQueue<int>();
        await using var supervisor = new ProcessSupervisor(new("NORTHPASS_READY protocol=1", null, TimeSpan.FromSeconds(1), TimeSpan.Zero));
        supervisor.StatusChanged += s => { if (s.ProcessId is int pid) pids.Enqueue(pid); };
        await Assert.ThrowsAsync<TimeoutException>(() => supervisor.StartAsync(ProcessSupervisorTests.Child("protocol-hang")));
        Assert.Equal(EngineState.Error, (await supervisor.GetStatusAsync()).State); Assert.Null((await supervisor.GetStatusAsync()).ProcessId);
        foreach (int pid in pids.Distinct()) Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        // Native-style fixture supplies a stdout readiness line; with no graceful
        // stop protocol, owned termination should not mislabel its exit a crash.
        await supervisor.StartAsync(ProcessSupervisorTests.Child("protocol"));
        Assert.Equal(EngineState.Active, (await supervisor.GetStatusAsync()).State);
        await supervisor.StopAsync(); Assert.Equal(EngineState.Disconnected, (await supervisor.GetStatusAsync()).State);
    }
    [Fact]
    public async Task StderrReadinessIsRejectedAndCancellationCleansOwnedChild()
    {
        await using var supervisor = new ProcessSupervisor(new("NORTHPASS_READY protocol=1", null, TimeSpan.FromSeconds(1), TimeSpan.Zero));
        await Assert.ThrowsAsync<TimeoutException>(() => supervisor.StartAsync(ProcessSupervisorTests.Child("protocol-stderr")));
        using var cancel = new CancellationTokenSource();
        supervisor.StatusChanged += s => { if (s.State == EngineState.Connecting && s.ProcessId is not null) cancel.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => supervisor.StartAsync(ProcessSupervisorTests.Child("protocol-hang"), cancel.Token));
        Assert.Null((await supervisor.GetStatusAsync()).ProcessId);
    }
}
