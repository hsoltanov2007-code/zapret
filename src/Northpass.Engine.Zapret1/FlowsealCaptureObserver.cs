using Northpass.Models;

namespace Northpass.Engine.Zapret1;

// Exact status messages audited against the pinned v72.9 source. Debug packet
// logging stays off. This observer cannot confirm matches or transformations.
public sealed class FlowsealCaptureObserver
{
    // Applied before both live logging and retained startup-error evidence.
    public static string? SafeLog(string line) => line.Contains("try to disable secure boot", StringComparison.OrdinalIgnoreCase) ? null : line;
    public const string ReadyLine = "windivert initialized. capture is started.";
    private TrafficEvidence _evidence = new(CaptureState.Unconfirmed, "", "", "");
    public TrafficEvidence Evidence => Volatile.Read(ref _evidence);
    public void Reset(StrategyProfile profile, bool disabled)
    {
        var strategy = FlowsealCatalog.Find(profile.StrategyId) ?? throw new InvalidDataException("Unreviewed strategy.");
        string Ports(string name) => strategy.Global.Single(o => o.Name == name).Value
            .Replace("{GAME_TCP}", profile.GameTcpPorts, StringComparison.Ordinal)
            .Replace("{GAME_UDP}", profile.GameUdpPorts, StringComparison.Ordinal);
        Volatile.Write(ref _evidence, new(disabled ? CaptureState.TestDisabled : CaptureState.Unconfirmed,
            strategy.Id, Ports("--wf-tcp"), Ports("--wf-udp")));
    }
    public EngineStatus Apply(EngineStatus state)
    {
        var evidence = Evidence;
        if (state.State == EngineState.Disconnected) evidence = evidence with { Capture = CaptureState.Unconfirmed };
        if (state.State == EngineState.Error && state.ProcessId is null && evidence.Capture == CaptureState.Initialized)
            evidence = evidence with { Capture = CaptureState.Failed };
        if (state.State == EngineState.Active && evidence.Capture is CaptureState.Unavailable or CaptureState.Failed)
            state = state with { State = EngineState.Error, Error = "Network capture is unavailable. Disconnect and reconnect; service access is unverified." };
        return state with { Traffic = evidence };
    }
    public void Observe(string line)
    {
        if (Evidence.Capture == CaptureState.TestDisabled) return;
        CaptureState? next = line switch
        {
            ReadyLine => CaptureState.Initialized,
            "logical network disappeared. deinitializing windivert." or
            "logical network is not present. waiting it to appear." => CaptureState.Unavailable,
            _ when line.StartsWith("[stderr] windivert: recv failed. errno ", StringComparison.Ordinal) ||
                   line.StartsWith("[stderr] windivert: error opening filter", StringComparison.Ordinal) ||
                   line.StartsWith("[stderr] windivert: reinject of packet id=", StringComparison.Ordinal) => CaptureState.Failed,
            _ => null
        };
        if (next is { } capture) Volatile.Write(ref _evidence, Evidence with { Capture = capture });
    }
}
