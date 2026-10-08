using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Northpass.Models;

namespace Northpass.Services;

public enum ProbeState { Unknown, Passed, Failed }
public sealed record ProbeObservation(ProbeState State, string Detail, double Milliseconds = 0);
public sealed record ServiceProbeTarget(string Id, string Name, Uri HttpsUrl);
public sealed record ServiceProbeResult(string Id, string Name, string Host, IReadOnlyList<string> ResolvedAddresses,
    string? ConnectedAddress, ProbeObservation Dns, ProbeObservation Tcp, ProbeObservation Tls, ProbeObservation Https,
    ProbeObservation Quic, ProbeObservation Stun, ProbeObservation MediaPlayback, ProbeObservation Login, ProbeObservation Voice, ProbeObservation Gateway);
public enum ServiceAvailability { Unknown, Available, Limited, Unavailable }
public static class ServiceAvailabilityPolicy
{
    // These labels describe only the checked web endpoint, never the engine state.
    public static ServiceAvailability Evaluate(ServiceProbeResult? result)
    {
        if (result is null) return ServiceAvailability.Unknown;
        if (result.Dns.State == ProbeState.Failed || result.Tcp.State == ProbeState.Failed) return ServiceAvailability.Unavailable;
        var stages = new[] { result.Dns, result.Tcp, result.Tls, result.Https };
        if (stages.All(stage => stage.State == ProbeState.Passed)) return ServiceAvailability.Available;
        if (stages.Any(stage => stage.State != ProbeState.Unknown)) return ServiceAvailability.Limited;
        return ServiceAvailability.Unknown;
    }
}
public interface IServiceProbeTransport
{
    Task<ServiceProbeResult> ProbeAsync(ServiceProbeTarget target, CancellationToken token);
}
public sealed class ServiceProbeService(IServiceProbeTransport transport)
{
    public static IReadOnlyList<ServiceProbeTarget> Targets { get; } = Array.AsReadOnly(new[]
    {
        new ServiceProbeTarget("youtube", "YouTube", new("https://www.youtube.com/")),
        new ServiceProbeTarget("discord", "Discord", new("https://discord.com/api/v10/gateway")),
        new ServiceProbeTarget("telegram", "Telegram", new("https://web.telegram.org/"))
    });
    public async Task<IReadOnlyList<ServiceProbeResult>> TestAsync(CancellationToken token = default)
    {
        var results = await Task.WhenAll(Targets.Select(target => transport.ProbeAsync(target, token)));
        token.ThrowIfCancellationRequested();
        return Array.AsReadOnly(results);
    }
}

// Uses one resolved address, TCP socket and authenticated TLS stream for each
// HTTP/1.1 status check. No DNS/hosts/firewall changes, cookies, credentials,
// redirects, WebSocket login, QUIC or STUN claims.
public sealed class NetworkServiceProbeTransport : IServiceProbeTransport
{
    public async Task<ServiceProbeResult> ProbeAsync(ServiceProbeTarget target, CancellationToken token)
    {
        if (!ServiceProbeService.Targets.Contains(target)) throw new ArgumentException("Only the compiled service probe targets are allowed.");
        var unknown = new ProbeObservation(ProbeState.Unknown, "Not tested. No conclusion about streaming, login, gateway negotiation, UDP/QUIC or voice.");
        var dns = unknown; var tcp = unknown; var tls = unknown; var https = unknown;
        IPAddress[] addresses = []; string? connected = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var watch = Stopwatch.StartNew(); string stage = "dns";
        try
        {
            addresses = await Dns.GetHostAddressesAsync(target.HttpsUrl.Host, timeout.Token);
            if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
            dns = new(ProbeState.Passed, $"Resolved {addresses.Length} IPv4/IPv6 addresses. Resolution alone does not prove access.", watch.Elapsed.TotalMilliseconds);
            stage = "tcp"; watch.Restart();
            TcpClient? client = null;
            try
            {
                Exception? last = null;
                foreach (var address in addresses.Take(8))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var candidate = new TcpClient(address.AddressFamily);
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); attempt.CancelAfter(TimeSpan.FromSeconds(4));
                    try { await candidate.ConnectAsync(address, 443, attempt.Token); client = candidate; connected = address.ToString(); break; }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException) { candidate.Dispose(); last = ex; }
                }
                if (client is null) throw last ?? new IOException("No address accepted TCP port 443.");
                tcp = new(ProbeState.Passed, "TCP 443 opened to " + connected + ". Other IP families/addresses remain untested.", watch.Elapsed.TotalMilliseconds);
                stage = "tls"; watch.Restart();
                using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = target.HttpsUrl.Host, EnabledSslProtocols = SslProtocols.None,
                    ApplicationProtocols = [SslApplicationProtocol.Http11]
                }, timeout.Token);
                tls = new(ProbeState.Passed, "TLS certificate and hostname verified on the connected address.", watch.Elapsed.TotalMilliseconds);
                stage = "https"; watch.Restart();
                byte[] request = Encoding.ASCII.GetBytes($"GET {target.HttpsUrl.PathAndQuery} HTTP/1.1\r\nHost: {target.HttpsUrl.Host}\r\nUser-Agent: Northpass/0.6\r\nAccept: */*\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(request, timeout.Token);
                await stream.FlushAsync(timeout.Token);
                var status = new List<byte>(); byte[] one = new byte[1]; bool completeLine = false;
                while (status.Count < 4096)
                {
                    int read = await stream.ReadAsync(one, timeout.Token);
                    if (read == 0) break;
                    if (one[0] == '\n') { completeLine = true; break; }
                    status.Add(one[0]);
                }
                string line = Encoding.ASCII.GetString(status.ToArray()).TrimEnd('\r');
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!completeLine || parts.Length < 2 || parts[0] is not ("HTTP/1.0" or "HTTP/1.1") || !int.TryParse(parts[1], out int code) || code is < 100 or > 599)
                    throw new IOException("Endpoint did not return a bounded valid HTTP/1.1 status line.");
                https = new(code is >= 200 and < 300 ? ProbeState.Passed : ProbeState.Failed,
                    $"HTTP {code} from {target.HttpsUrl}. This host/status only; no media, login, voice or provider-wide bypass conclusion.", watch.Elapsed.TotalMilliseconds);
            }
            finally { client?.Dispose(); }
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            var failed = new ProbeObservation(ProbeState.Failed, ex.Message.Length > 2048 ? ex.Message[..2048] : ex.Message, watch.Elapsed.TotalMilliseconds);
            if (stage == "dns") dns = failed; else if (stage == "tcp") tcp = failed; else if (stage == "tls") tls = failed; else https = failed;
        }
        return new(target.Id, target.Name, target.HttpsUrl.Host, Array.AsReadOnly(addresses.Select(a => a.ToString()).ToArray()),
            connected, dns, tcp, tls, https, unknown, unknown, unknown, unknown, unknown, unknown);
    }
}

public sealed record StrategyTestRecord(string Id, DateTimeOffset StartedAt, string Provider, string ProfileId, string EngineId,
    string StrategyId, string InstalledRevision, EngineStatus EngineBeforeProbes, EngineStatus EngineAfterProbes,
    IReadOnlyList<ServiceProbeResult> Services, IReadOnlyList<string> Logs, string? Failure = null,
    ProbeState UserReportedPlayback = ProbeState.Unknown, ProbeState UserReportedVoice = ProbeState.Unknown, StrategyProfile? Inputs = null);
public sealed class StrategyTestRecordStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public void Save(StrategyTestRecord record)
    {
        if (!Guid.TryParseExact(record.Id, "N", out _) || record.Provider.Length > 120 || record.Provider.Any(char.IsControl)) throw new InvalidDataException("Test record metadata is invalid.");
        string data = System.Text.Json.JsonSerializer.Serialize(record, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        if (data.Length > 2 * 1024 * 1024) throw new InvalidDataException("Test evidence exceeds its size bound.");
        Installation.SafeArchive.NoLinks(DirectoryPath); Directory.CreateDirectory(DirectoryPath);
        AtomicFile.Write(Path.Combine(DirectoryPath, record.Id + ".json"), data);
    }
}

public sealed class StrategyTestRunner(EngineController controller, ServiceProbeService probes, StrategyTestRecordStore records)
{
    public async Task<StrategyTestRecord> RunAsync(StrategyProfile profile, string provider,
        Func<StrategyProfile, CancellationToken, Task<(EngineConfiguration Configuration, string Revision)>> prepare,
        CancellationToken token = default)
    {
        if (provider.Length > 120 || provider.Any(char.IsControl)) throw new ArgumentException("Provider label must contain at most 120 non-control characters.");
        var logs = new Queue<string>(); object sync = new();
        void Log(string line) { lock (sync) { logs.Enqueue(line.Length > 4096 ? line[..4096] : line); if (logs.Count > 128) logs.Dequeue(); } }
        var before = new EngineStatus(EngineState.Disconnected); var after = before;
        IReadOnlyList<ServiceProbeResult> results = []; string revision = "", failure = "";
        StrategyTestRecord? record = null;
        var snapshot = ProfileValidation.Copy(profile); var started = DateTimeOffset.UtcNow;
        controller.LogReceived += Log;
        try
        {
            // No new child or network test starts before the old owned session stops.
            await controller.DisconnectAsync(token);
            var prepared = await prepare(snapshot, token); revision = prepared.Revision;
            var validation = await controller.ValidateAsync(prepared.Configuration, token);
            if (!validation.IsValid) throw new InvalidOperationException(validation.Summary);
            await controller.ConnectAsync(prepared.Configuration, autoRecover: false, token);
            before = await controller.GetStatusAsync(token);
            if (before.State != EngineState.Active) throw new InvalidOperationException("The engine did not remain active for strategy testing.");
            results = await probes.TestAsync(token);
            after = await controller.GetStatusAsync(token);
            if (after.State != EngineState.Active) failure = "Engine exited during testing; endpoint outcomes do not certify this strategy.";
        }
        catch (Exception ex) { failure = ex.Message; throw; }
        finally
        {
            try { await controller.DisconnectAsync(CancellationToken.None); }
            finally
            {
                controller.LogReceived -= Log;
                string[] lines; lock (sync) lines = logs.ToArray();
                record = new StrategyTestRecord(Guid.NewGuid().ToString("N"), started, provider, snapshot.Id, snapshot.Engine,
                    snapshot.StrategyId, revision, before, after, results, Array.AsReadOnly(lines), string.IsNullOrEmpty(failure) ? null : failure, Inputs: snapshot);
                records.Save(record);
            }
        }
        return record!;
    }
}
