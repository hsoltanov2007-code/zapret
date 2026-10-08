namespace Northpass.Models;

public sealed class StrategyProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Engine { get; set; } = "zapret2";
    public List<string> Arguments { get; set; } = new();
    // Reviewed catalog reference and data-only inputs; never shell/environment variables.
    public string StrategyId { get; set; } = "";
    public string GameTcpPorts { get; set; } = "12";
    public string GameUdpPorts { get; set; } = "12";
    public Dictionary<string, string> ListBindings { get; set; } = new(StringComparer.Ordinal);

    // Not part of JSON; set when loaded from disk.
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourcePath { get; set; } = "";

    public override string ToString() => Name;
}
