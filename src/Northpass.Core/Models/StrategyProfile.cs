namespace Northpass.Models;

public sealed class StrategyProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Engine { get; set; } = "zapret2";
    public List<string> Arguments { get; set; } = new();

    // Not part of JSON; set when loaded from disk.
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourcePath { get; set; } = "";

    public override string ToString() => Name;
}
