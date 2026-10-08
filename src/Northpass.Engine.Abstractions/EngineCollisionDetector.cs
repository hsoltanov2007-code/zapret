using System.Diagnostics;

namespace Northpass.Engine;

public sealed class EngineConflictException(string message) : InvalidOperationException(message);
public interface IEngineCollisionDetector { void Check(int? ownedProcessId = null); }
public sealed class EngineCollisionDetector : IEngineCollisionDetector
{
    public void Check(int? ownedProcessId = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (string name in new[] { "winws", "winws2", "NorthpassCore" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                {
                    try
                    {
                        if (process.Id != ownedProcessId && !process.HasExited)
                            throw new EngineConflictException($"Another network application ({name}, PID {process.Id}) is running. Close it through its own controls, then retry. Northpass will not kill it or change shared driver services.");
                    }
                    catch (InvalidOperationException ex) when (ex is not EngineConflictException) { /* process exited during enumeration */ }
                }
    }
}
