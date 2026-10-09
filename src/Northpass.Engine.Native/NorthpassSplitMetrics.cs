using System.Globalization;
namespace Northpass.Engine.Native;

// Internal laboratory diagnostics; endpoint reconstruction is measured by the
// endpoint harness, never inferred from a driver send or running process.
public sealed record NorthpassSplitPerformance(ulong Proposed, ulong Accepted, ulong Rejected,
    ulong LabReinjections, ulong SendFailures, ulong KnownDropped, bool KernelLossUnknown,
    bool ReconstructionUnknown, ulong TracePackets, double ProcessingLatencyMicroseconds);

public static class NorthpassSplitMetrics
{
    public static NorthpassSplitPerformance Parse(string line)
    {
        const string prefix = "NORTHPASS_SPLIT_METRICS ";
        if (line.Length > 1024 || !line.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Invalid split metrics frame.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in line[prefix.Length..].Split(' '))
        {
            var pair = field.Split('=');
            if (pair.Length != 2 || !values.TryAdd(pair[0], pair[1])) throw new InvalidDataException("Invalid/duplicate split metric.");
        }
        if (values.Count != 11 || values.GetValueOrDefault("protocol") != "4" || values.GetValueOrDefault("kernel_loss_unknown") != "1" ||
            values.GetValueOrDefault("reconstruction_unknown") != "1") throw new InvalidDataException("Unsupported split diagnostics schema.");
        ulong Count(string key) => values.TryGetValue(key, out var text) && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= 1_000_000_000_000
            ? value : throw new InvalidDataException("Invalid split counter: " + key);
        ulong proposed = Count("proposed"), accepted = Count("accepted"), rejected = Count("rejected"), reinjections = Count("lab_reinjections");
        if (accepted > proposed || rejected > proposed - accepted || reinjections > accepted * 16) throw new InvalidDataException("Inconsistent split counters.");
        if (!values.TryGetValue("latency_us", out var latency) || !double.TryParse(latency, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var measurement) ||
            !double.IsFinite(measurement) || measurement < 0 || measurement > 1e12) throw new InvalidDataException("Invalid split latency.");
        return new(proposed, accepted, rejected, reinjections, Count("send_failures"), Count("dropped_known"), true, true, Count("trace_packets"), measurement);
    }
}
