namespace Northpass.Models;

public enum EngineState { Disconnected, Connecting, Active, Stopping, Error }
public sealed record EngineStatus(EngineState State, int? ProcessId = null,
    DateTimeOffset? StartedAt = null, int? ExitCode = null, string? Error = null);
public sealed record EngineConfiguration(string ExecutablePath, StrategyProfile Profile);
public sealed record ValidationIssue(string Code, string Message);
public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
    public string Summary => IsValid ? "Configuration is valid." : string.Join(Environment.NewLine, Issues.Select(i => i.Message));
    public static ValidationResult Valid { get; } = new(Array.Empty<ValidationIssue>());
}
public sealed record EngineDescriptor(string Id, string Name, string ExecutableName);
public sealed record ReachabilityResult(Uri Url, bool Reachable, int? HttpStatus, TimeSpan Duration, string Detail);
