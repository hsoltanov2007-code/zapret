using Northpass.Models;
using Northpass.Services;

namespace Northpass.Desktop;

public interface IDesktopServices
{
    string? PickDataList() => null;
    string? PickExport(string name, string extension);
    bool ConfirmTrust(string message);
    Task SetAutoStartAsync(bool enabled);
}
