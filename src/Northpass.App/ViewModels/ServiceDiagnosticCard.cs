using Northpass.Services;

namespace Northpass.ViewModels;

public sealed class ServiceDiagnosticCard(ServiceProbeTarget target, string language) : ObservableObject
{
    private string _language = language;
    private ServiceProbeResult? _result;
    private DateTimeOffset? _checkedAt;
    public string Id => target.Id;
    public string Name => target.Name;
    public ServiceAvailability Availability => ServiceAvailabilityPolicy.Evaluate(_result);
    public string Summary => new UiStrings(_language)[Availability.ToString()];
    public string Scope => new UiStrings(_language)["WebCheck"];
    public string Detail => _result is null ? new UiStrings(_language)["Unknown"] :
        string.Join("\n", new[] { ("DNS", _result.Dns), ("TCP", _result.Tcp), ("TLS", _result.Tls), ("HTTPS", _result.Https) }
            .Select(stage => $"{stage.Item1}: {new UiStrings(_language)[stage.Item2.State.ToString()]} · {stage.Item2.Milliseconds:F0} ms · {stage.Item2.Detail}")) +
        "\n" + _result.Host + " · " + (_result.ConnectedAddress ?? "—");
    public string Color => Availability switch { ServiceAvailability.Available => "#A7C1B3", ServiceAvailability.Limited => "#C8B58E", ServiceAvailability.Unavailable => "#CF9D95", _ => "#94949E" };
    public DateTimeOffset? CheckedAt => _checkedAt;
    public void Apply(ServiceProbeResult? result)
    {
        _result = result; _checkedAt = result is null ? null : DateTimeOffset.UtcNow;
        Notify();
    }
    public void Localize(string language) { _language = language; Notify(); }
    private void Notify()
    { foreach (string name in new[] { nameof(Availability), nameof(Summary), nameof(Scope), nameof(Detail), nameof(Color), nameof(CheckedAt) }) Changed(name); }
}

public sealed class StrategyChoice(Northpass.Models.StrategyProfile profile, string language) : ObservableObject
{
    private string _language = language;
    public Northpass.Models.StrategyProfile Profile { get; } = profile;
    public string Label => new UiStrings(_language)["Strategy_" + Profile.StrategyId] +
        (Profile.Id == "flowseal-" + Profile.StrategyId ? "" : " · " + new UiStrings(_language)["Custom"]);
    public void Localize(string language) { _language = language; Changed(nameof(Label)); }
}
