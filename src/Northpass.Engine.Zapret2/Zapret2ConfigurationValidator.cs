using System.Text.RegularExpressions;
using Northpass.Models;

namespace Northpass.Engine.Zapret2;

public static partial class Zapret2ConfigurationValidator
{
    public const string ReviewedSourceCommit = "a1adf7b868e65c8a77aab608c8bbd9ec8b30a256";
    public static readonly string[] RequiredFiles = ["cygwin1.dll", "WinDivert.dll", "WinDivert64.sys"];
    private static readonly HashSet<string> FileOptions = new(StringComparer.Ordinal)
        { "--hostlist", "--hostlist-exclude", "--ipset", "--ipset-exclude" };
    private static readonly HashSet<string> ForegroundOnly = new(StringComparer.Ordinal)
        { "--daemon", "--pidfile", "--chdir", "--fuzz", "--help", "--version", "--dry-run", "--wf-save", "--nlm-list" };

    public static ValidationResult Validate(EngineConfiguration configuration)
    {
        var profile = configuration.Profile;
        var issues = ProfileValidation.Validate(profile).Issues.ToList();
        if (!string.Equals(profile.Engine, "zapret2", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("engine.id", "This adapter accepts only Zapret2 profiles."));
        string executable;
        try { executable = Path.GetFullPath(configuration.ExecutablePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { issues.Add(new("engine.path", "Select a valid engine executable path.")); return new(issues); }
        if (!string.Equals(Path.GetFileName(executable), "winws2.exe", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("engine.name", "Select winws2.exe from the official Windows bundle."));
        if (!File.Exists(executable)) issues.Add(new("engine.missing", "Engine executable does not exist: " + executable));
        string engineDir = Path.GetDirectoryName(executable)!;
        foreach (string name in RequiredFiles)
            if (!ExistsCaseInsensitive(engineDir, name)) issues.Add(new("engine.dependency", "Missing engine dependency: " + name));
        if (string.IsNullOrWhiteSpace(profile.SourcePath))
        { issues.Add(new("profile.source", "Save this profile before starting it.")); return new(issues); }
        string profileDir;
        try { profileDir = Path.GetDirectoryName(Path.GetFullPath(profile.SourcePath))!; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { issues.Add(new("profile.source", "Invalid profile path.")); return new(issues); }
        if (profile.Arguments is null || issues.Any(i => i.Code == "profile.argument")) return new(issues);
        for (int i = 0; i < profile.Arguments.Count; i++)
        {
            string argument = Expand(profile.Arguments[i], engineDir, profileDir);
            string option = argument.Split('=', 2)[0];
            string? value = argument.Contains('=') ? argument[(argument.IndexOf('=') + 1)..] : null;
            if (Placeholder().IsMatch(argument)) issues.Add(new("argument.placeholder", "Unknown path placeholder in argument: " + argument));
            if (!option.StartsWith("--", StringComparison.Ordinal))
            { issues.Add(new("argument.option", "Use one --option=value argument per JSON item; batch/config files and commands are not supported.")); continue; }
            if (option.StartsWith("--dpi-desync", StringComparison.Ordinal))
                issues.Add(new("argument.zapret1", "Zapret1 --dpi-desync options are not valid Zapret2 strategies."));
            if (!Zapret2Options.Known.TryGetValue(option, out string? valueKind))
                issues.Add(new("argument.unknown", "Unknown or abbreviated option for the reviewed Zapret2 version: " + option));
            else if (valueKind == "required_argument" && string.IsNullOrEmpty(value))
                issues.Add(new("argument.value", "Required value must use --option=value syntax: " + option));
            else if (valueKind == "no_argument" && value is not null)
                issues.Add(new("argument.value", "This option does not accept a value: " + option));
            if (ForegroundOnly.Contains(option) || option is "--intercept" or "--wf-dup-check" && value is not null && value != "1")
                issues.Add(new("argument.lifecycle", "Option is incompatible with supervised interception: " + option));
            string? file = null;
            if (FileOptions.Contains(option)) file = value;
            else if (option == "--lua-init")
            {
                if (value is null || !value.StartsWith('@'))
                    issues.Add(new("argument.lua", "Inline Lua is disabled. Use --lua-init=@{ENGINE_DIR}/path.lua from a trusted bundle."));
                else file = value[1..];
            }
            else if (option is "--wf-raw" or "--wf-raw-part" or "--wf-raw-filter" && value?.StartsWith('@') == true) file = value[1..];
            else if (option == "--blob" && value is not null && value.Contains('@')) file = value[(value.IndexOf('@') + 1)..];
            if (file is null && FileOptions.Contains(option)) issues.Add(new("argument.file", "File option requires --option=filename: " + option));
            if (file is not null)
            {
                if (string.IsNullOrWhiteSpace(file)) { issues.Add(new("argument.file", "Empty file reference: " + option)); continue; }
                try
                {
                    string fullPath = Path.GetFullPath(file, engineDir);
                    if (!File.Exists(fullPath) && !File.Exists(fullPath + ".gz"))
                        issues.Add(new("argument.file", "Referenced file is missing: " + fullPath));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                { issues.Add(new("argument.file", "Invalid file reference: " + file)); }
            }
        }
        return new(issues);
    }

    private static bool ExistsCaseInsensitive(string directory, string name) => Directory.Exists(directory) &&
        Directory.EnumerateFiles(directory).Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
    public static string Expand(string value, string engineDirectory, string profileDirectory) => value
        .Replace("{ENGINE_DIR}", engineDirectory, StringComparison.OrdinalIgnoreCase)
        .Replace("{PROFILE_DIR}", profileDirectory, StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex(@"\{[A-Z_]+\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
