using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Northpass.Desktop;
using Northpass.Engine.Native;
using Northpass.Models;
using Northpass.Services.Installation;
namespace Northpass.Windows.Tests;

public sealed class NativeSplitLabWindowsTests
{
    private static string Repository()
    {
        for (var p = new DirectoryInfo(AppContext.BaseDirectory); p is not null; p = p.Parent)
            if (File.Exists(Path.Combine(p.FullName, "Northpass.sln"))) return p.FullName;
        throw new DirectoryNotFoundException();
    }
    private static async Task Stop(Process p)
    {
        if (!p.HasExited) { await p.StandardInput.WriteLineAsync("STOP"); await p.StandardInput.FlushAsync(); }
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    }
    private static ulong Field(string line, string key) => ulong.Parse(line.Split(' ').Single(x => x.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..], CultureInfo.InvariantCulture);
    private static X509Certificate2 SchannelCertificate(CertificateRequest request)
    {
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
        try {
            // Schannel rejects ephemeral private-key handles. Import only into
            // the current user's temporary key container; no certificate store
            // or PersistKeySet. Dispose deletes the imported private key.
            return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet);
        } finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    private static async Task Exchange(IPAddress address, int port, TcpListener listener, X509Certificate2 certificate, SslProtocols protocol, int size, CancellationToken token, Func<Task>? duringTraffic = null)
    {
        var accept = listener.AcceptTcpClientAsync(token).AsTask();
        using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        await client.ConnectAsync(address, port, token);
        using var server = await accept; server.NoDelay = true;
        using var serverTls = new SslStream(server.GetStream());
        using var clientTls = new SslStream(client.GetStream(), false, (_, remote, _, _) => remote is not null && CryptographicOperations.FixedTimeEquals(remote.GetRawCertData(), certificate.RawData));
        var serverHandshake = serverTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
            ServerCertificate = certificate, EnabledSslProtocols = protocol, ClientCertificateRequired = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, token);
        var clientHandshake = clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "northpass-lab.invalid", EnabledSslProtocols = protocol, CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, token);
        // Surface either endpoint's first failure immediately; awaiting only the
        // client could hide a server credential error behind a network timeout.
        var first = await Task.WhenAny(serverHandshake, clientHandshake);
        await first;
        await Task.WhenAll(serverHandshake, clientHandshake);
        Assert.True(serverTls.IsAuthenticated && clientTls.IsAuthenticated); Assert.Equal(protocol, clientTls.SslProtocol);
        byte[] pattern = Enumerable.Range(0, size).Select(x => (byte)(x * 31)).ToArray(), received = new byte[size];
        // Concurrent reads/writes prevent a large test from depending on socket buffers.
        var request = clientTls.WriteAsync(pattern, token).AsTask();
        if (duringTraffic is not null) await duringTraffic();
        await serverTls.ReadExactlyAsync(received, token); await request; Assert.Equal(pattern, received);
        var response = serverTls.WriteAsync(received, token).AsTask();
        await clientTls.ReadExactlyAsync(received, token); await response; Assert.Equal(pattern, received);
    }
    [Fact]
    public async Task ActualLabPartialSendFailuresCancellationAndHardDeadlineCleanUp()
    {
        string repo = Repository(); using var catalog = NativeCatalog.OpenTrustedManifest(); using var http = new HttpClient();
        var manager = new EngineInstallationManager(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-Native-0.4"), http,
            new WindowsInstallationSecurity(), EngineManifest.Parse(catalog), offlinePayload: Path.Combine(repo, "dist/native/native-offline.zip"), probe: NativeEngine.VerifyInstalledVersionAsync, requireOfflinePayload: true);
        var installed = await manager.EnsureInstalledAsync(); using var key = RSA.Create(2048);
        using var certificate = SchannelCertificate(new CertificateRequest("CN=northpass-lab.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var evidence = new List<string>();
        foreach (string mode in new[] { "send-first", "send-second", "cancel-active", "hard-stop" })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var info = NativeEngine.CreateStartInfo(new(installed.ExecutablePath, NativeCatalog.Loopback(port, "tcp")));
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            foreach (var arg in new[] { "--test-capability", "--lab-split", "--lab-seconds", mode == "hard-stop" ? "1" : "20" }) info.ArgumentList.Add(arg);
            if (mode.StartsWith("send-", StringComparison.Ordinal)) { info.ArgumentList.Add("--lab-send-failure"); info.ArgumentList.Add(mode == "send-first" ? "1" : "2"); }
            await using var lease = await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath);
            using var process = Process.Start(info)!; var lines = new ConcurrentQueue<string>(); var errors = process.StandardError.ReadToEndAsync(); Task? output = null;
            try
            {
                Assert.Equal("NORTHPASS_READY protocol=1", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                output = Task.Run(async () => { string? line; while ((line = await process.StandardOutput.ReadLineAsync()) is not null) lines.Enqueue(line); });
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                if (mode == "cancel-active")
                {
                    await Exchange(IPAddress.Loopback, port, listener, certificate, SslProtocols.Tls12, 32768, deadline.Token, () => Stop(process));
                    await output; Assert.True(process.ExitCode == 0, await errors);
                    Assert.True(NorthpassSplitMetrics.Parse(lines.Last(x => x.StartsWith("NORTHPASS_SPLIT_METRICS ", StringComparison.Ordinal))).Accepted >= 1);
                    evidence.Add("STOP during authenticated TLS application write: exact 32768-byte bidirectional reconstruction continued after actual driver cleanup.");
                }
                else if (mode == "hard-stop")
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); await output; Assert.True(process.ExitCode == 0, await errors);
                    await Exchange(IPAddress.Loopback, port, listener, certificate, SslProtocols.Tls12, 4096, deadline.Token);
                    evidence.Add("One-second nonextendable hard deadline stopped the actual idle lab worker; authenticated TLS/reconstruction then succeeded without interception.");
                }
                else
                {
                    var exchange = Exchange(IPAddress.Loopback, port, listener, certificate, SslProtocols.Tls12, 4096, deadline.Token);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); await output;
                    Assert.NotEqual(0, process.ExitCode);
                    var metrics = NorthpassSplitMetrics.Parse(lines.Last(x => x.StartsWith("NORTHPASS_SPLIT_METRICS ", StringComparison.Ordinal)));
                    Assert.Equal(1ul, metrics.SendFailures); Assert.True(metrics.KnownDropped >= 1 && metrics.KernelLossUnknown);
                    Assert.Equal(mode == "send-first" ? 0ul : 1ul, metrics.LabReinjections);
                    Assert.Contains(lines, x => x.StartsWith("NORTHPASS_LAB_SEND_FAILURE ", StringComparison.Ordinal) && x.Contains("rollback_allowed=0", StringComparison.Ordinal));
                    // A TCP retransmission after interception stops may recover or
                    // fail; neither result turns an ambiguous send into rollback.
                    bool endpointRecovered;
                    try { await exchange; endpointRecovered = true; }
                    catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException) { endpointRecovered = false; }
                    evidence.Add($"{mode}: injected suppressed actual lab segment, reinjections={metrics.LabReinjections}, known_drop>=1, rollback forbidden, worker nonzero, endpoint_recovered={endpointRecovered}. Not a naturally occurring driver send failure.");
                }
            }
            catch (Exception failure)
            {
                try { await Stop(process); } catch (Exception) { if (!process.HasExited) process.Kill(); }
                if (output is not null) await output;
                throw new InvalidOperationException($"Lab fault mode={mode}; native exit={(process.HasExited ? process.ExitCode : -999)} stderr={await errors}; trace={string.Join('\n', lines)}", failure);
            }
            finally { listener.Stop(); if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } if (output is not null) await output; }
        }
        await using var recovery = new NativeEngine(manager); await recovery.StartAsync(new(installed.ExecutablePath, NativeCatalog.Idle())); await recovery.StopAsync();
        Directory.CreateDirectory(Path.Combine(repo, "TestResults")); File.WriteAllLines(Path.Combine(repo, "TestResults/engine-native-split-faults.txt"), evidence);
    }
    [Fact]
    public async Task ActualIsolatedIpv4Ipv6TlsAndSplitPacketTracesPreserveReconstruction()
    {
        string repo = Repository(); using var catalog = NativeCatalog.OpenTrustedManifest(); using var http = new HttpClient();
        var manager = new EngineInstallationManager(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-Native-0.4"), http,
            new WindowsInstallationSecurity(), EngineManifest.Parse(catalog), offlinePayload: Path.Combine(repo, "dist/native/native-offline.zip"), probe: NativeEngine.VerifyInstalledVersionAsync, requireOfflinePayload: true);
        var installed = await manager.EnsureInstalledAsync();
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=northpass-lab.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("northpass-lab.invalid"); certificateRequest.CertificateExtensions.Add(san.Build());
        using var certificate = SchannelCertificate(certificateRequest);
        var evidence = new List<object>(); var measurements = new List<string>(); string outputDirectory = Path.Combine(repo, "TestResults"); Directory.CreateDirectory(outputDirectory);
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        foreach (var protocol in new[] { SslProtocols.Tls12, SslProtocols.Tls13 })
        foreach (bool split in new[] { false, true })
        {
            var listener = new TcpListener(address, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; Assert.InRange(port, 49152, 65535);
            var info = NativeEngine.CreateStartInfo(new(installed.ExecutablePath, NativeCatalog.Loopback(port, "tcp")));
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            if (split) foreach (var arg in new[] { "--test-capability", "--lab-split", "--lab-seconds", "20" }) info.ArgumentList.Add(arg);
            await using var lease = await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath);
            using var process = Process.Start(info)!; var lines = new ConcurrentQueue<string>(); var errors = process.StandardError.ReadToEndAsync();
            Task? output = null; var watch = Stopwatch.StartNew();
            try
            {
                Assert.Equal("NORTHPASS_READY protocol=1", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                output = Task.Run(async () => { string? line; while ((line = await process.StandardOutput.ReadLineAsync()) is not null) lines.Enqueue(line); });
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                foreach (int size in new[] { 1, 4096, 32768 }) await Exchange(address, port, listener, certificate, protocol, size, deadline.Token);
                await Stop(process); await output; Assert.True(process.ExitCode == 0, await errors);
                var native = NativeMetrics.Parse(lines.Last(x => x.StartsWith("NORTHPASS_METRICS ", StringComparison.Ordinal)));
                Assert.Equal(0ul, native.KnownDropped); Assert.True(native.KernelLossUnknown);
                NorthpassSplitPerformance? metrics = null;
                if (split)
                {
                    metrics = NorthpassSplitMetrics.Parse(lines.Last(x => x.StartsWith("NORTHPASS_SPLIT_METRICS ", StringComparison.Ordinal)));
                    Assert.True(metrics.Accepted >= 3 && metrics.LabReinjections >= 6, string.Join('\n', lines));
                    Assert.Equal(0ul, metrics.SendFailures);
                    var originals = lines.Where(x => x.StartsWith("NORTHPASS_LAB_TRACE stage=original ", StringComparison.Ordinal)).ToArray();
                    var sent = lines.Where(x => x.StartsWith("NORTHPASS_LAB_TRACE stage=sent ", StringComparison.Ordinal)).ToArray();
                    var post = lines.Where(x => x.StartsWith("NORTHPASS_LAB_TRACE stage=post ", StringComparison.Ordinal)).ToArray();
                    Assert.True(post.Length > 0, "Actual post-injection sniff trace absent.");
                    foreach (var original in originals)
                    {
                        uint sequence = (uint)Field(original, "seq"); ulong remaining = Field(original, "payload");
                        int count = (int)Field(original, "segments");
                        var segments = sent.Where(x => Field(x, "ack") == Field(original, "ack") && unchecked((uint)(Field(x, "seq") - sequence)) < remaining).Take(count).ToArray();
                        Assert.Equal(count, segments.Length);
                        foreach (var segment in segments)
                        {
                            Assert.Equal(sequence, (uint)Field(segment, "seq")); Assert.Equal(0ul, Field(segment, "hello"));
                            Assert.InRange(Field(segment, "bytes"), 1ul, 1280ul);
                            Assert.Contains(post, x => Field(x, "seq") == Field(segment, "seq") && Field(x, "payload") == Field(segment, "payload") && Field(x, "ack") == Field(segment, "ack") && Field(x, "checksum_valid") == 1);
                            var length = Field(segment, "payload"); remaining -= length; sequence = unchecked(sequence + (uint)length);
                        }
                        Assert.Equal(0ul, remaining);
                    }
                }
                string name = $"lab-{address.AddressFamily}-{protocol}-{(split ? "split" : "original")}";
                File.WriteAllLines(Path.Combine(outputDirectory, name + "-trace.txt"), lines);
                evidence.Add(new { name, authenticatedTls = true, reconstruction = "three bidirectional fixed-pattern exchanges matched exactly", sizes = new[] { 1, 4096, 32768 }, metrics,
                    native, wallMilliseconds = watch.Elapsed.TotalMilliseconds, scope = "dedicated loopback only", simulatorIsNotIspEvidence = true });
                measurements.Add(FormattableString.Invariant($"ACTUAL {name}: accepted={metrics?.Accepted ?? 0} lab_send_successes={metrics?.LabReinjections ?? 0} proposal_latency_us={metrics?.ProcessingLatencyMicroseconds ?? 0:F3} cpu_percent={native.CpuPercent:F3} private_bytes={native.MemoryBytes} known_drops={native.KnownDropped} kernel_loss_unknown=true endpoint_reconstruction=three_exact_bidirectional_exchanges wall_ms={watch.Elapsed.TotalMilliseconds:F3}"));
            }
            catch (Exception failure)
            {
                try { await Stop(process); } catch (Exception) { if (!process.HasExited) process.Kill(); }
                if (output is not null) await output;
                File.WriteAllLines(Path.Combine(outputDirectory, $"lab-failed-{address.AddressFamily}-{protocol}-{split}-trace.txt"), lines);
                throw new InvalidOperationException($"Lab address={address} protocol={protocol} split={split}; native exit={(process.HasExited ? process.ExitCode : -999)} stderr={await errors}; trace={string.Join('\n', lines)}", failure);
            }
            finally { listener.Stop(); if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } if (output is not null) await output; }
        }
        File.WriteAllText(Path.Combine(outputDirectory, "lab-tls-results.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outputDirectory, "engine-native-split-lab.txt"), "Actual protected NorthpassCore executable/WinDivert loopback lab: IPv4/IPv6 TLS 1.2/1.3 baseline and experimental split authenticated; three bidirectional 1/4096/32768-byte exchanges per session reconstructed exactly. Independent lower-priority sniff handle compared post-injection sequence/length/checksum traces with committed proposals. Toy simulator results do not prove any ISP bypass. See lab-tls-results.json and lab-*-trace.txt.\n" + string.Join('\n', measurements));
    }
}
