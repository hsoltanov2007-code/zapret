using Northpass.Models;

namespace Northpass.Engine;

public interface IDpiEngine : IAsyncDisposable
{
    EngineDescriptor Descriptor { get; }
    event Action<string>? LogReceived;
    event Action<EngineStatus>? StatusChanged;
    Task StartAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task RestartAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default);
    Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ValidationResult> ValidateConfigurationAsync(EngineConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class EngineRegistry
{
    private readonly Dictionary<string, (EngineDescriptor Descriptor, Func<IDpiEngine> Factory)> _entries = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<EngineDescriptor> Available => _entries.Values.Select(e => e.Descriptor).ToArray();
    public void Register(EngineDescriptor descriptor, Func<IDpiEngine> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!_entries.TryAdd(descriptor.Id, (descriptor, factory)))
            throw new InvalidOperationException($"Engine '{descriptor.Id}' is already registered.");
    }
    public IDpiEngine Create(string id) => _entries.TryGetValue(id, out var entry)
        ? entry.Factory() : throw new NotSupportedException($"Engine '{id}' is not installed.");
    public EngineDescriptor? Find(string id) => _entries.TryGetValue(id, out var entry) ? entry.Descriptor : null;
}
