namespace Northpass.Engine;

public sealed record InstalledEngine(string EngineId, string Revision, string Version, string ExecutablePath, string SourceRevision = "");
public sealed record InstallationProgress(string Phase, long Completed, long Total)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(100d * Completed / Total, 0, 100);
}
public sealed record EngineUpdateStatus(string InstalledRevision, string ReviewedRevision, bool CanUpdate, string Detail);

// Installation is separate from the traffic engine: a native adapter need not download anything.
public interface IEngineInstallationManager
{
    string EngineId { get; }
    Task<InstalledEngine?> DetectAsync(CancellationToken token = default);
    Task<InstalledEngine> EnsureInstalledAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default);
    Task<EngineUpdateStatus> CheckForUpdatesAsync(CancellationToken token = default);
    Task<InstalledEngine> UpdateAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default);
    Task<InstalledEngine> RepairAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default);
    Task<InstalledEngine> RollbackAsync(CancellationToken token = default);
    Task<IAsyncDisposable> AcquireLaunchLeaseAsync(string executablePath, CancellationToken token = default);
}
