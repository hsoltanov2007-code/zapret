using Northpass.Models;
using Northpass.Services;

namespace Northpass.Desktop;

public interface IDesktopServices
{
    string? PickEngine(EngineDescriptor descriptor);
    string? PickProfile();
    string? PickExport(string name, string extension);
    bool ConfirmTrust(string message);
    StrategyProfile? EditProfile(ProfileStore store, StrategyProfile? original);
    Task SetAutoStartAsync(bool enabled);
}
