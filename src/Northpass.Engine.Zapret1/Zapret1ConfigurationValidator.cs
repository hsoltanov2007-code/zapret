using Northpass.Models;

namespace Northpass.Engine.Zapret1;

public static class Zapret1ConfigurationValidator
{
    public static ValidationResult Validate(EngineConfiguration configuration)
    {
        var profile = configuration.Profile;
        var issues = ProfileValidation.Validate(profile).Issues.ToList();
        if (!string.Equals(profile.Engine, "zapret1", StringComparison.OrdinalIgnoreCase)) issues.Add(new("engine.id", "This adapter accepts Zapret1 profiles only."));
        var strategy = FlowsealCatalog.Find(profile.StrategyId ?? "");
        if (strategy is null) issues.Add(new("strategy.unreviewed", "Choose a compiled reviewed Flowseal strategy. Runtime BAT/config/strategy imports are not allowed."));
        else if (profile.Arguments is null || !profile.Arguments.SequenceEqual(FlowsealCatalog.Templates(strategy), StringComparer.Ordinal))
            issues.Add(new("strategy.modified", "Strategy arguments differ from the reviewed definition. Only typed port/list inputs may change."));
        if (!Zapret1Options.ValidPorts(profile.GameTcpPorts) || !Zapret1Options.ValidPorts(profile.GameUdpPorts))
            issues.Add(new("strategy.ports", "Game ports must be numeric ports/ranges between 1 and 65535. Default 12 preserves Flowseal's disabled GameFilter sentinel."));
        string bin;
        try
        {
            string exe = Path.GetFullPath(configuration.ExecutablePath); bin = Path.GetDirectoryName(exe)!;
            if (!Path.GetFileName(exe).Equals("winws.exe", StringComparison.OrdinalIgnoreCase)) issues.Add(new("engine.name", "Zapret1 requires the verified winws.exe, not winws2.exe or a command interpreter."));
            if (!File.Exists(exe)) issues.Add(new("engine.missing", "Verified Zapret1 executable is missing."));
            foreach (string name in new[] { "cygwin1.dll", "WinDivert.dll", "WinDivert64.sys" })
                if (!File.Exists(Path.Combine(bin, name))) issues.Add(new("engine.dependency", "Required runtime/driver component is missing: " + name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { issues.Add(new("engine.path", "Invalid verified engine path.")); return new(issues); }
        if (strategy is not null)
            foreach (var option in strategy.Global.Concat(strategy.Rules.SelectMany(rule => rule)))
            {
                string? path = option.Kind switch
                {
                    StrategyValueKind.BinAsset => Path.Combine(bin, option.Value),
                    StrategyValueKind.BundleList => Path.Combine(Path.GetDirectoryName(bin)!, "lists", option.Value), _ => null
                };
                if (path is not null && !File.Exists(path)) issues.Add(new("strategy.asset", "Reviewed strategy data is missing: " + option.Value));
            }
        return new(issues);
    }
}
