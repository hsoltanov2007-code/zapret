using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Northpass.Engine;
using Northpass.Engine.Zapret1;
using Northpass.Models;
using Northpass.Services;
namespace Northpass.Broker;

public sealed record BrokerRequest(int Version, uint Sequence, string Command, string Engine = "zapret1", string Strategy = "",
    int? Port = null, string Transport = "both", string TcpPorts = "12", string UdpPorts = "12", Dictionary<string,string>? Lists = null);
public sealed record BrokerResponse(int Version, uint Sequence, bool Success, string Error = "", InstalledEngine? Installed = null,
    EngineStatus? Status = null, EnginePerformance? Performance = null, string[]? Logs = null, bool NoTrafficTest = false, int SafeCode = 0);
public static class BrokerProtocol
{
    public const int MaximumFrame = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static void Validate(BrokerRequest r, uint expected)
    {
        if (r.Version != 3 || r.Sequence != expected || expected == 0 || r.Engine is not ("zapret1" or "native") ||
            r.Command is not ("DETECT" or "INSTALL" or "REPAIR" or "ROLLBACK" or "START" or "STOP" or "STATUS" or "METRICS" or "SHUTDOWN"))
            throw new InvalidDataException("Unsupported broker command, version or replayed sequence.");
        if (r.Command != "START" && (r.Strategy != "" || r.Port is not null || r.Transport != "both" || r.TcpPorts != "12" || r.UdpPorts != "12" || r.Lists is not null))
            throw new InvalidDataException("Unexpected broker command fields.");
        if (r.Command != "START") return;
        if(r.TcpPorts is null || r.UdpPorts is null || r.TcpPorts.Length>1024 || r.UdpPorts.Length>1024)throw new InvalidDataException("Invalid port configuration size.");
        if (r.Strategy is null || r.Strategy.Length>64) throw new InvalidDataException("Invalid strategy identifier.");
        if (r.Engine == "native")
        {
            if (r.Strategy is not ("passthrough-idle" or "passthrough-loopback") || r.TcpPorts != "12" || r.UdpPorts != "12" || r.Lists is not null ||
                r.Strategy == "passthrough-idle" && (r.Port is not null || r.Transport != "both") ||
                r.Strategy == "passthrough-loopback" && (r.Port is not (>=49152 and <=65535) || r.Transport is not ("tcp" or "udp" or "both")))
                throw new InvalidDataException("Invalid native test scope.");
        }
        else
        {
            if (FlowsealCatalog.Find(r.Strategy) is null || r.Port is not null || r.Transport != "both" ||
                !Zapret1Options.ValidPorts(r.TcpPorts) || !Zapret1Options.ValidPorts(r.UdpPorts)) throw new InvalidDataException("Invalid reviewed strategy inputs.");
            if (r.Lists is { Count: >4 }) throw new InvalidDataException("Too many data-only lists.");
            foreach (var (slot,text) in r.Lists ?? [])
            {
                if(text is null || text.Length>DataListStore.MaximumBytes)throw new InvalidDataException("Invalid data-only list size.");
                var kind = slot switch { "general" or "excluded-hosts" => DataListKind.Hosts, "excluded-ips" or "all-ips" => DataListKind.IpSet, _ => throw new InvalidDataException("Unknown list slot.") };
                _ = DataListStore.Normalize(text,kind);
            }
        }
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value,Json);
        if (bytes.Length is <1 or >MaximumFrame) throw new InvalidDataException("Broker frame exceeds bounds.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await stream.WriteAsync(header,token); await stream.WriteAsync(bytes,token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream,CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header,token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <1 or >MaximumFrame) throw new InvalidDataException("Broker frame length rejected.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes,deadline.Token);
        // Duplicate properties are rejected before deserialization (JSON otherwise takes the last value).
        using var doc = JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth = 8 });
        CheckNames(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes,Json) ?? throw new InvalidDataException("Empty broker message.");
    }
    private static void CheckNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject()) { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate broker field."); CheckNames(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach(var item in element.EnumerateArray()) CheckNames(item);
    }
}
