namespace Northpass.Models;

// Numeric counters only: never contains packet bytes, addresses, domains or flow keys.
public sealed record EnginePerformance(double CpuPercent, ulong MemoryBytes, double PacketsPerSecond,
    double LatencyMicroseconds, double MaximumLatencyMicroseconds, ulong QueueSize, ulong QueueCapacity,
    ulong QueuePeak, ulong Captured, ulong Forwarded, ulong KnownDropped, bool KernelLossUnknown,
    ulong Backpressure, ulong ActiveFlows, ulong RecoverableErrors, ulong FatalErrors, ulong AuthenticationFailures);

public interface IEnginePerformanceProvider
{
    Task<EnginePerformance?> GetPerformanceAsync(CancellationToken cancellationToken = default);
}
