using Northpass.Models;
using Northpass.Services;

namespace Northpass.Desktop;

public interface IDesktopServices
{
    string? PickEngine(EngineDescriptor descriptor);
    string? PickProfile();
    string? PickDataList() => null;
    string? PickExport(string name, string extension);
    bool ConfirmTrust(string message);
    StrategyProfile? EditProfile(ProfileStore store, StrategyProfile? original);
    Task SetAutoStartAsync(bool enabled);
}
