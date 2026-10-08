namespace Northpass.Models;

public sealed class AppSettings
{
    public bool EngineSetupConsent { get; set; }
    public string EnginePath { get; set; } = "";
    public string SelectedProfileId { get; set; } = "";
    public string Language { get; set; } = "en";
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutoRecover { get; set; }
    public bool CheckForUpdates { get; set; }
    public string DiagnosticUrl { get; set; } = "https://example.com/";
}
