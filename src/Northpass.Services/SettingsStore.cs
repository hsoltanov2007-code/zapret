using System.Text.Json;
using Northpass.Models;
namespace Northpass.Services;

public sealed class SettingsStore(string folder)
{
    public string Folder { get; } = Path.GetFullPath(folder);
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Northpass");
    public AppSettings Load()
    {
        string path = Path.Combine(Folder, "settings.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), ProfileValidation.JsonOptions)
            ?? throw new FormatException("settings.json must be an object.");
    }
    public void Save(AppSettings settings) => AtomicFile.Write(Path.Combine(Folder, "settings.json"),
        JsonSerializer.Serialize(settings, ProfileValidation.JsonOptions));
}

internal static class AtomicFile
{
    public static void Write(string path, string content, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Refusing to overwrite a symbolic link: " + path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
