using Northpass.Models;
namespace Northpass.Services;

// Architecture seam for Phase 2. Not implemented in this starter.
public sealed class NativeEngineAdapter : IEngineAdapter
{
    public string EngineId => "native";
    public bool IsRunning => false;
    public event Action<string>? LogReceived;
    public event Action? Exited;
    public void Start(string executablePath, StrategyProfile profile) =>
        throw new NotSupportedException("Собственный движок появится на этапе 2.");
    public void Stop() { }
    public void Dispose() { }
}
