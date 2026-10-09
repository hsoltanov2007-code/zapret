using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Northpass.Models;

namespace Northpass.Engine.Zapret1;

public enum StrategyValueKind { Literal, BinAsset, BundleList, UserList, Ports }
public sealed record StrategyOption(string Name, StrategyValueKind Kind, string Value);
public sealed record FlowsealStrategy(string Id, string Name, string SourceFile, string SourceSha256,
    IReadOnlyList<StrategyOption> Global, IReadOnlyList<IReadOnlyList<StrategyOption>> Rules);

// Compiled trust root. No BAT parsing or remote strategy updates happen in the app.
public static class FlowsealCatalog
{
    public const string BundleCommit = "865da4f4c3659523bf79bc6edf0446e7d7969614";
    public const string SourceCommit = "c849e55ef0f1c244206f5a05ff7b1ab41a3824ee";
    public const string StarterProfileId = "flowseal-general";
    public static Stream OpenTrustedManifest() => Resource("Northpass.Flowseal.TrustedManifest");
    public static IEnumerable<Stream> OpenPreviousTrustedManifests() => typeof(FlowsealCatalog).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("Northpass.Flowseal.Previous.", StringComparison.Ordinal)).Select(Resource);
    private static Stream Resource(string name) => typeof(FlowsealCatalog).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("Reviewed Flowseal catalog is missing.");
    private sealed record Document(string BundleRevision, string BundleVersion, List<Definition> Strategies);
    private sealed record Definition(string Id, string Name, string SourceFile, string SourceSha256,
        List<StrategyOption> Global, List<List<StrategyOption>> Rules);
    private static readonly FrozenDictionary<string, FlowsealStrategy> Entries = Load();
    public static IReadOnlyList<FlowsealStrategy> Strategies { get; } = Array.AsReadOnly(Entries.Values.OrderBy(s => s.Id).ToArray());
    public static IReadOnlySet<string> Assets { get; } = LoadAssets();
    private static FrozenDictionary<string, FlowsealStrategy> Load()
    {
        using var stream = Resource("Northpass.Flowseal.Strategies");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        var document = JsonSerializer.Deserialize<Document>(stream, options) ?? throw new InvalidDataException("Empty strategy catalog.");
        if (document.BundleRevision != BundleCommit || document.BundleVersion != "1.10.3") throw new InvalidDataException("Strategy pin differs from the reviewed bundle.");
        return document.Strategies.Select(s => new FlowsealStrategy(s.Id, s.Name, s.SourceFile, s.SourceSha256,
            Array.AsReadOnly(s.Global.ToArray()), Array.AsReadOnly(s.Rules.Select(r => (IReadOnlyList<StrategyOption>)Array.AsReadOnly(r.ToArray())).ToArray())))
            .ToFrozenDictionary(s => s.Id, StringComparer.Ordinal);
    }
    private static FrozenSet<string> LoadAssets()
    {
        using var stream = OpenTrustedManifest(); using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("path").GetString()!).ToFrozenSet(StringComparer.Ordinal);
    }
    public static FlowsealStrategy? Find(string id) => Entries.GetValueOrDefault(id);
    public static StrategyProfile Profile(FlowsealStrategy strategy) => new()
    {
        Id = "flowseal-" + strategy.Id, Name = strategy.Name, Engine = "zapret1", StrategyId = strategy.Id,
        Description = $"Reviewed Flowseal 1.10.3: {strategy.SourceFile}. YouTube/Discord/voice filters included; effectiveness must be tested on your provider.",
        Arguments = Templates(strategy).ToList()
    };
    public static IReadOnlyList<string> Templates(FlowsealStrategy strategy) => Compile(strategy,
        "{ENGINE_DIR}", "{LISTS_DIR}", "{DATA_DIR}", "{GAME_TCP}", "{GAME_UDP}", false, includeTelegram: false);
    public static IReadOnlyList<string> Compile(FlowsealStrategy strategy, string binDirectory, string listsDirectory,
        string dataDirectory, string tcpPorts, string udpPorts, bool noTrafficCapture = false, string? allIpsPath = null, bool includeTelegram = true)
    {
        var result = new List<string>();
        string Resolve(StrategyOption option)
        {
            if (!Zapret1Options.Known.Contains(option.Name)) throw new InvalidDataException("Unreviewed strategy option: " + option.Name);
            string value = option.Kind switch
            {
                StrategyValueKind.BinAsset => Join(binDirectory, option.Value),
                StrategyValueKind.BundleList => option.Value == "ipset-all.txt" && allIpsPath is not null ? allIpsPath : Join(listsDirectory, option.Value),
                StrategyValueKind.UserList => Join(dataDirectory, UserListName(option.Value)),
                StrategyValueKind.Ports => option.Value.Replace("{GAME_TCP}", tcpPorts, StringComparison.Ordinal).Replace("{GAME_UDP}", udpPorts, StringComparison.Ordinal),
                _ => option.Value
            };
            return option.Name + "=" + value;
        }
        if (noTrafficCapture) result.Add("--wf-raw=false"); // Explicit test composition, never a profile/shell input.
        else result.AddRange(strategy.Global.Select(Resolve));
        // Telegram comes first ONLY on the compiled exact destination set.
        // Broad custom game/IP sets must not shadow opaque MTProto handling.
        if (includeTelegram) { result.AddRange(TelegramRule()); result.Add("--ipset-exclude=" + Join(listsDirectory,"ipset-exclude.txt")); result.Add("--ipset-exclude=" + Join(dataDirectory,"ipset-exclude-user.txt")); }
        for (int rule = 0; rule < strategy.Rules.Count; rule++)
        {
            if (rule > 0 || includeTelegram) result.Add("--new");
            result.AddRange(strategy.Rules[rule].Select(Resolve));
        }
        return Array.AsReadOnly(result.ToArray());
    }
    public static IReadOnlyList<string> TelegramRule() => Array.AsReadOnly(new[] {
        "--filter-tcp=80,443", "--ipset-ip=" + TelegramEndpoints.IpSet,
        "--dpi-desync=multisplit", "--dpi-desync-split-pos=1",
        "--dpi-desync-any-protocol=1", "--dpi-desync-cutoff=d2"
    });
    private static string Join(string root, string name)
    {
        if (name.Contains('/') || name.Contains('\\') || name.Contains(':') || name is "." or "..") throw new InvalidDataException("Catalog asset name is invalid.");
        // Templates use '/' on every OS; resolved runtime paths use Path.Combine.
        return root.StartsWith('{') ? root + "/" + name : Path.Combine(root, name);
    }
    public static string UserListName(string slot) => slot switch
    {
        "general" => "list-general-user.txt", "excluded-hosts" => "list-exclude-user.txt",
        "excluded-ips" => "ipset-exclude-user.txt", _ => throw new InvalidDataException("Unknown data-only list slot.")
    };
}
