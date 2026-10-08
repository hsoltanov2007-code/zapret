using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Northpass.Models;
using Northpass.Services;

namespace Northpass;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private IEngineAdapter _engine = new Zapret2EngineAdapter();
    private List<StrategyProfile> _profiles = new();
    private readonly Brush _idle = new SolidColorBrush(Color.FromRgb(139, 146, 154));
    private readonly Brush _active = new SolidColorBrush(Color.FromRgb(142, 185, 162));

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        EnginePathBox.Text = _settings.EnginePath;
        _engine.LogReceived += Engine_LogReceived;
        _engine.Exited += Engine_Exited;
        ReloadProfiles();
        WriteLog("Northpass 0.1 · выбран адаптер Zapret2.");
        WriteLog("Добавьте официальный winws2.exe и настройте файл профиля.");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Выберите winws2.exe",
            Filter = "Zapret2 engine (winws2.exe)|winws2.exe|Executable (*.exe)|*.exe"
        };
        if (picker.ShowDialog(this) == true)
        {
            EnginePathBox.Text = picker.FileName;
            _settings.EnginePath = picker.FileName;
            SaveSettings();
            WriteLog("Путь к движку сохранён.");
        }
    }

    private void ReloadProfiles_Click(object sender, RoutedEventArgs e) => ReloadProfiles();

    private void ReloadProfiles()
    {
        if (_engine.IsRunning)
        {
            WriteLog("Сначала остановите движок, затем обновите профили.");
            return;
        }
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "profiles");
        Directory.CreateDirectory(folder);
        _profiles.Clear();
        foreach (string file in Directory.GetFiles(folder, "*.json").OrderBy(s => s))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<StrategyProfile>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name))
                    throw new FormatException("Id или Name не заполнены");
                profile.SourcePath = file;
                _profiles.Add(profile);
            }
            catch (Exception ex) { WriteLog($"Не удалось загрузить {System.IO.Path.GetFileName(file)}: {ex.Message}"); }
        }
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = _profiles;
        ProfileCombo.SelectedItem = _profiles.FirstOrDefault(p => p.Id == _settings.SelectedProfileId)
                                    ?? _profiles.FirstOrDefault();
        UpdateProfileInfo();
        WriteLog($"Загружено профилей: {_profiles.Count}.");
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings is null) return;
        _settings.SelectedProfileId = (ProfileCombo.SelectedItem as StrategyProfile)?.Id ?? "";
        SaveSettings();
        UpdateProfileInfo();
    }

    private void UpdateProfileInfo()
    {
        var selected = ProfileCombo.SelectedItem as StrategyProfile;
        ProfileDescription.Text = selected is null ? "Нет профилей." :
            $"{selected.Name} · {selected.Description} · аргументов: {selected.Arguments?.Count ?? 0}";
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = ProfileCombo.SelectedItem as StrategyProfile
                ?? throw new InvalidOperationException("Нет выбранного профиля.");
            if (!string.Equals(profile.Engine, "zapret2", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Пока поддерживается только zapret2.");
            string enginePath = EnginePathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(enginePath))
                throw new InvalidOperationException("Выберите путь к winws2.exe.");
            _settings.EnginePath = enginePath;
            _settings.SelectedProfileId = profile.Id;
            SaveSettings();
            _engine.Start(enginePath, profile);
            SetRunning(true);
        }
        catch (Exception ex)
        {
            WriteLog("Ошибка запуска: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Northpass · ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { _engine.Stop(); WriteLog("Движок остановлен пользователем."); }
        catch (Exception ex) { WriteLog("Ошибка остановки: " + ex.Message); }
        SetRunning(false);
    }

    private void Engine_LogReceived(string message) => Dispatcher.BeginInvoke((Action)(() => WriteLog(message)));
    private void Engine_Exited() => Dispatcher.BeginInvoke((Action)(() => SetRunning(false)));

    private void SetRunning(bool running)
    {
        StatusDot.Fill = running ? _active : _idle;
        StatusText.Text = running ? "Движок запущен" : "Не запущен";
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        EnginePathBox.IsEnabled = !running;
        ProfileCombo.IsEnabled = !running;
    }

    private void WriteLog(string message)
    {
        if (LogBox is null) return;
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void SaveSettings()
    {
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { WriteLog("Не удалось сохранить настройки: " + ex.Message); }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    protected override void OnClosed(EventArgs e)
    {
        _engine.Dispose(); // Stop packet interception when the application exits.
        base.OnClosed(e);
    }
}
