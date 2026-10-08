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
    public async Task SetAutoStartAsync(bool enabled)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the app executable.");
        if (!string.Equals(System.IO.Path.GetFileName(executable), "Northpass.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Autostart requires a published Northpass.exe in a stable location; publish before enabling it.");
        string user = WindowsIdentity.GetCurrent().Name;
        string taskName = "Northpass-" + WindowsIdentity.GetCurrent().User!.Value;
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string[] arguments = enabled
            ? ["/Create", "/TN", taskName, "/SC", "ONLOGON", "/RU", user, "/IT", "/RL", "HIGHEST", "/TR", "\"" + executable + "\" --tray", "/F"]
            : ["/Delete", "/TN", taskName, "/F"];
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Windows Task Scheduler tool.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Windows Task Scheduler did not respond within ten seconds.");
        }
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw new IOException("Autostart configuration failed: " + await error + " " + await output);
    }
}
