using System.Text.Json;
using Northpass.Models;

namespace Northpass.Services;

public sealed record ProfileLoadResult(IReadOnlyList<StrategyProfile> Profiles, IReadOnlyList<string> Errors);
public sealed class ProfileStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public ProfileLoadResult Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        var profiles = new List<StrategyProfile>();
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var profile = Read(file);
                if (!ids.Add(profile.Id)) throw new FormatException("Duplicate profile Id: " + profile.Id);
                profile.SourcePath = file;
                profiles.Add(profile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
            { errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
        }
        return new(profiles, errors);
    }
    private static StrategyProfile Read(string file)
    {
        if (new FileInfo(file).Length > 1024 * 1024) throw new FormatException("Profile exceeds the 1 MiB limit.");
        return ProfileValidation.Parse(File.ReadAllText(file));
    }
    public StrategyProfile Save(StrategyProfile profile)
    {
        var result = ProfileValidation.Validate(profile, requireArguments: false);
        if (!result.IsValid) throw new FormatException(result.Summary);
        if (Load().Profiles.Any(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A profile with this Id already exists: " + profile.Id);
        string path = Path.Combine(DirectoryPath, profile.Id + ".json");
        AtomicFile.Write(path, JsonSerializer.Serialize(profile, ProfileValidation.JsonOptions), overwrite: false);
        profile.SourcePath = path;
        return profile;
    }
    public StrategyProfile Update(StrategyProfile original, StrategyProfile replacement)
    {
        if (replacement.Id != original.Id) throw new FormatException("The Id of an existing profile cannot change; create a new profile instead.");
        var validation = ProfileValidation.Validate(replacement, requireArguments: false);
        if (!validation.IsValid) throw new FormatException(validation.Summary);
        string path = Path.GetFullPath(original.SourcePath);
        if (!string.Equals(Path.GetDirectoryName(path), DirectoryPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Only profiles in the user profile directory may be edited.");
        AtomicFile.Write(path, JsonSerializer.Serialize(replacement, ProfileValidation.JsonOptions));
        replacement.SourcePath = path;
        return replacement;
    }
    public StrategyProfile Import(string file) => Save(Read(file));
    public void Export(StrategyProfile profile, string destination) =>
        AtomicFile.Write(Path.GetFullPath(destination), JsonSerializer.Serialize(profile, ProfileValidation.JsonOptions));
    public void Seed(string bundledDirectory)
    {
        if (!Directory.Exists(bundledDirectory)) return;
        Directory.CreateDirectory(DirectoryPath);
        foreach (string source in Directory.EnumerateFiles(bundledDirectory, "*.json"))
        {
            string target = Path.Combine(DirectoryPath, Path.GetFileName(source));
            if (!File.Exists(target)) File.Copy(source, target, overwrite: false);
        }
    }
}
