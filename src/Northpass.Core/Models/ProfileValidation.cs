using System.Text.Json;

namespace Northpass.Models;

public static class ProfileValidation
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static StrategyProfile Parse(string json)
    {
        var profile = JsonSerializer.Deserialize<StrategyProfile>(json, JsonOptions)
            ?? throw new FormatException("Profile must be a JSON object.");
        var result = Validate(profile, requireArguments: false);
        if (!result.IsValid) throw new FormatException(result.Summary);
        return profile;
    }
    public static ValidationResult Validate(StrategyProfile profile, bool requireArguments = true)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 80 ||
            profile.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            issues.Add(new("profile.id", "Id must contain 1–80 ASCII letters, digits, '-' or '_'."));
        if (string.IsNullOrWhiteSpace(profile.Name)) issues.Add(new("profile.name", "Profile Name is required."));
        if (string.IsNullOrWhiteSpace(profile.Engine)) issues.Add(new("profile.engine", "Engine id is required."));
        if (profile.Arguments is null) issues.Add(new("profile.arguments", "Arguments must be an array."));
        else
        {
            if (requireArguments && profile.Arguments.Count == 0)
                issues.Add(new("profile.empty", "This draft has no arguments. Configure a verified strategy before connecting."));
            if (profile.Arguments.Count > 512) issues.Add(new("profile.limit", "A profile may contain at most 512 arguments."));
            if (profile.Arguments.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 32768 || a.Any(c => c is '\0' or '\r' or '\n')))
                issues.Add(new("profile.argument", "Arguments must be nonempty single strings without NUL or newlines."));
        }
        return new(issues);
    }
    public static StrategyProfile Copy(StrategyProfile profile) => new()
    {
        Id = profile.Id, Name = profile.Name, Description = profile.Description,
        Engine = profile.Engine, Arguments = profile.Arguments.ToList(), SourcePath = profile.SourcePath
    };
}
