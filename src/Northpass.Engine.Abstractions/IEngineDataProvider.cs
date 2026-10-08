using Northpass.Models;

namespace Northpass.Engine;

// Mutable user data crosses the privilege boundary only after data validation,
// through a protected, immutable session snapshot. Never executable code.
public interface IEngineDataProvider
{
    Task<ValidationResult> ValidateAsync(StrategyProfile profile, CancellationToken token = default);
    Task<IEngineDataLease> PrepareAsync(StrategyProfile profile, CancellationToken token = default);
}
public interface IEngineDataLease : IAsyncDisposable
{
    string DirectoryPath { get; }
    string? AllIpsPath { get; }
}
