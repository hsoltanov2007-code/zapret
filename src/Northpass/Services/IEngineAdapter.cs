using Northpass.Models;
namespace Northpass.Services;

public interface IEngineAdapter : IDisposable
{
    string EngineId { get; }
    bool IsRunning { get; }
    event Action<string>? LogReceived;
    event Action? Exited;
    void Start(string executablePath, StrategyProfile profile);
    void Stop();
}
