using Northpass.Models;
namespace Northpass.Engine.Native;

public static class NativeCatalog
{
    public static bool IsBundled => typeof(NativeCatalog).Assembly.GetManifestResourceNames().Contains("Northpass.Native.Manifest");
    public static Stream OpenTrustedManifest() => typeof(NativeCatalog).Assembly.GetManifestResourceStream("Northpass.Native.Manifest")
        ?? throw new InvalidOperationException("Build and package NorthpassCore before compiling the Windows application.");
    public static StrategyProfile Idle() => new() { Id = "native-idle", Name = "Native pass-through test", Engine = "native", StrategyId = "passthrough-idle" };
    public static StrategyProfile Loopback(int port, string transport = "both") => new() {
        Id = "native-loopback", Name = "Native isolated loopback test", Engine = "native", StrategyId = "passthrough-loopback", Native = new(port, transport)
    };
    public static ValidationResult Validate(EngineConfiguration configuration)
    {
        var issues = new List<ValidationIssue>(); var p = configuration.Profile;
        if (p.Engine != "native") issues.Add(new("NativeEngine", "A native profile is required."));
        if (p.StrategyId is not ("passthrough-idle" or "passthrough-loopback")) issues.Add(new("NativeStrategy", "Native v0.1 supports only idle and scoped loopback pass-through tests; it does not bypass DPI."));
        if (p.Arguments is null || p.Arguments.Count != 0 || p.ListBindings is null || p.ListBindings.Count != 0 || p.GameTcpPorts != "12" || p.GameUdpPorts != "12")
            issues.Add(new("NativeInputs", "Native v0.1 accepts no raw arguments, lists or game port overrides."));
        if (p.StrategyId == "passthrough-loopback" && (p.Native?.LoopbackPort is not (>= 49152 and <= 65535) || p.Native.Transport is not ("tcp" or "udp" or "both")))
            issues.Add(new("NativeScope", "An explicitly reserved loopback port 49152..65535 and TCP/UDP scope are required."));
        if (p.StrategyId == "passthrough-idle" && p.Native is not null && (p.Native.LoopbackPort is not null || p.Native.Transport != "both"))
            issues.Add(new("NativeScope", "Idle mode cannot intercept any traffic."));
        if (!Path.IsPathFullyQualified(configuration.ExecutablePath) || !Path.GetFileName(configuration.ExecutablePath).Equals("NorthpassCore.exe", StringComparison.OrdinalIgnoreCase))
            issues.Add(new("NativePath", "The protected NorthpassCore executable is required."));
        return new(issues);
    }
}
