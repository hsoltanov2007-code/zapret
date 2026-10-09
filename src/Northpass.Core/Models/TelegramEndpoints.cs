using System.Net;
using System.Text.Json;

namespace Northpass.Models;

public sealed record TelegramEndpoint(int Dc, string Address, int Port);

// Exact production bootstrap facts from the pinned OFFICIAL desktop source.
// No guessed subnets, DNS expansion, proxy endpoints or remote live updates.
public static class TelegramEndpoints
{
    public const string Revision = "86262333a457f62709726ee4ab9c48fa58824da4";
    public static IReadOnlyList<TelegramEndpoint> Bootstrap { get; } = Load();
    public static string IpSet => string.Join(',', Bootstrap.Select(e => IPAddress.Parse(e.Address).ToString()));
    public static bool Matches(IPAddress address, int port) => port is 80 or 443 && Bootstrap.Any(e => IPAddress.Parse(e.Address).Equals(address));
    private static IReadOnlyList<TelegramEndpoint> Load()
    {
        using var stream = typeof(TelegramEndpoints).Assembly.GetManifestResourceStream("Northpass.Telegram.Bootstrap") ?? throw new InvalidDataException("Telegram endpoint catalog missing.");
        using var doc = JsonDocument.Parse(stream); var root = doc.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("sourceRevision").GetString() != Revision ||
            root.GetProperty("scope").GetString() != "production-bootstrap-exact-addresses-only") throw new InvalidDataException("Unreviewed endpoint catalog.");
        var endpoints = root.GetProperty("endpoints").Deserialize<TelegramEndpoint[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (endpoints.Length != 11 || endpoints.Any(e => e.Dc is < 1 or > 5 || e.Port != 443 || !IPAddress.TryParse(e.Address, out _)) ||
            endpoints.Select(e => e.Address).Distinct().Count() != endpoints.Length) throw new InvalidDataException("Invalid endpoint catalog.");
        return Array.AsReadOnly(endpoints);
    }
}
