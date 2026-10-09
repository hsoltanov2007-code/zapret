using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
using Northpass.Models;
using Northpass.Services;
using Northpass.ViewModels;

namespace Northpass.Desktop;

public sealed class DesktopServices : IDesktopServices
{
    private static Window Owner => System.Windows.Application.Current.MainWindow;
    public string? PickDataList()
    {
        var dialog = new OpenFileDialog { Title = "Northpass", Filter = "Data lists (*.txt)|*.txt", CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
    public string? PickExport(string name, string extension)
    {
        var dialog = new SaveFileDialog { FileName = name + "." + extension, Filter = $"{extension.ToUpperInvariant()} files|*.{extension}", OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
    public bool ConfirmTrust(string message) => ProductDialog.Show(Owner, (Owner.DataContext as MainViewModel)?.Strings ?? new UiStrings("ru"), message);
    public Task SetAutoStartAsync(bool enabled)
    {
        string executable=Environment.ProcessPath??throw new InvalidOperationException("App executable unavailable.");
        if(!Path.GetFileName(executable).Equals("Northpass.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Autostart requires the installed Northpass app.");
        // Per-user startup remains unelevated. The privileged helper is requested
        // only when network initialization is needed, never through a highest task.
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(enabled)key.SetValue("Northpass","\""+executable+"\" --tray",RegistryValueKind.String);
        else key.DeleteValue("Northpass",throwOnMissingValue:false);
        return Task.CompletedTask;
    }
}
