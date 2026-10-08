using System.Diagnostics;
using Northpass.Models;

namespace Northpass.Services;

public sealed class Zapret2EngineAdapter : IEngineAdapter
{
    private Process? _process;
    public string EngineId => "zapret2";
    public bool IsRunning => _process is { HasExited: false };
    public event Action<string>? LogReceived;
    public event Action? Exited;

    public void Start(string executablePath, StrategyProfile profile)
    {
        if (IsRunning) throw new InvalidOperationException("Движок уже запущен.");
        if (!File.Exists(executablePath)) throw new FileNotFoundException("winws2.exe не найден", executablePath);
        if (!string.Equals(Path.GetFileName(executablePath), "winws2.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для этого адаптера выбери именно winws2.exe.");
        if (profile.Arguments is not { Count: > 0 })
            throw new InvalidOperationException("В профиле нет аргументов. Сначала настройте profiles/*.json.");

        string engineDir = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        string profileDir = Path.GetDirectoryName(Path.GetFullPath(profile.SourcePath))!;
        var psi = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = engineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Using ArgumentList prevents quoting errors and avoids command-shell execution.
        foreach (string item in profile.Arguments)
        {
            psi.ArgumentList.Add(item
                .Replace("{ENGINE_DIR}", engineDir, StringComparison.OrdinalIgnoreCase)
                .Replace("{PROFILE_DIR}", profileDir, StringComparison.OrdinalIgnoreCase));
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) LogReceived?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) LogReceived?.Invoke("[stderr] " + e.Data); };
        process.Exited += (_, _) =>
        {
            try { LogReceived?.Invoke($"winws2 завершился. Код: {process.ExitCode}"); }
            catch (InvalidOperationException) { LogReceived?.Invoke("winws2 завершился."); }
            Exited?.Invoke();
        };

        try
        {
            if (!process.Start()) throw new InvalidOperationException("Не удалось запустить winws2.exe.");
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            LogReceived?.Invoke("Запущен Zapret2 (PID " + process.Id + ").");
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        var process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { /* Already exited */ }
        finally
        {
            _process = null;
            process.Dispose();
        }
    }

    public void Dispose() => Stop();
}
