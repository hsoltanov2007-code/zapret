using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Zapret1;
using Northpass.Models;
using Northpass.Services;
using Northpass.Services.Installation;

namespace Northpass.Windows.Tests;

public sealed class FlowsealCaptureWindowsTests
{
    private static string Repository()
    {
        for (var p = new DirectoryInfo(AppContext.BaseDirectory); p is not null; p = p.Parent)
            if (File.Exists(Path.Combine(p.FullName, "Northpass.sln"))) return p.FullName;
        throw new DirectoryNotFoundException();
    }

    [Fact]
    public async Task RealWinwsCaptureForDedicatedLoopbackTcpUdpAndContainedReviewedUdpRule()
    {
        string repo = Repository(), root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-capture-test");
        var security = new WindowsInstallationSecurity();
        using var manifestStream = FlowsealCatalog.OpenTrustedManifest(); using var http = new HttpClient();
        var manager = new EngineInstallationManager(root, http, security, EngineManifest.Parse(manifestStream),
            offlinePayload: Path.Combine(repo, "dist/Northpass/engine-payload/flowseal-offline.zip"),
            probe: Zapret1Engine.VerifyInstalledVersionAsync, requireOfflinePayload: true);
        var installed = await manager.EnsureInstalledAsync();
        await using var lease = await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath);
        using var api = new Divert(Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!, "WinDivert.dll"));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port, udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        string user = Path.Combine(Path.GetTempPath(), "northpass-capture-" + Guid.NewGuid().ToString("N"));
        var store = new DataListStore(user);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var profile = FlowsealCatalog.Profile(FlowsealCatalog.Find("general")!);
            profile.GameUdpPorts = udpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Dedicated documentation address, never a public endpoint. This
            // validated IP-set activates the existing reviewed final UDP rule.
            Directory.CreateDirectory(user); string source = Path.Combine(user, "lab.txt"); File.WriteAllText(source, "203.0.113.77/32\n");
            profile.ListBindings["all-ips"] = store.Import(source, DataListKind.IpSet).Id;
            var provider = new ProtectedEngineDataProvider(store, Path.Combine(root, "lab-data"), security);
            await using var data = await provider.PrepareAsync(profile, deadline.Token);
            string loop = $"loopback and ((tcp and (tcp.DstPort == {tcpPort} or tcp.SrcPort == {tcpPort})) or (udp and (udp.DstPort == {udpPort} or udp.SrcPort == {udpPort})))";
            string synthetic = $"outbound and ip and ip.DstAddr == 203.0.113.77 and udp.DstPort == {udpPort}";
            // Lower-priority DROP handle contains every synthetic original/fake
            // BEFORE startup/injection. No synthetic packet can reach the NIC.
            using var guard = api.Handle(synthetic, 0, -1000, 2);
            using var syntheticSniff = api.Handle(synthetic, 0, -999, 5);
            using var sniff = api.Handle(loop, 0, -1000, 5);
            using var reflect = api.Handle("true", 4, 0, 5);
            using var injector = api.Handle("false", 0, 1000, 8);
            // Exact raw scope is test-only and is not reachable via broker START.
            var info = Zapret1Engine.CreateStartInfo(new(installed.ExecutablePath, profile), data);
            foreach (string arg in info.ArgumentList.Where(a => a.StartsWith("--wf-", StringComparison.Ordinal)).ToArray()) info.ArgumentList.Remove(arg);
            info.ArgumentList.Insert(0, "--wf-raw=(" + loop + ") or (" + synthetic + ")");
            info.RedirectStandardOutput = info.RedirectStandardError = true;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var process = Process.Start(info)!;
            var logs = new ConcurrentQueue<string>();
            var output = Task.Run(async () => { while (await process.StandardOutput.ReadLineAsync() is { } line) { if (line == FlowsealCaptureObserver.ReadyLine) ready.TrySetResult(); if (logs.Count < 32) logs.Enqueue(line); } });
            var errors = process.StandardError.ReadToEndAsync();
            int loopTcp = 0, loopUdp = 0, transformedPackets = 0; bool ownHandle = false, ownHandleClosed = false;
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reflection = Task.Run(() => {
                while (api.Receive(reflect.Value, out var packet, out var address))
                {
                    uint pid = BinaryPrimitives.ReadUInt32LittleEndian(address.AsSpan(24));
                    if (pid == process.Id && address[9] == 8 && BinaryPrimitives.ReadInt32LittleEndian(address.AsSpan(28)) == 0 &&
                        BinaryPrimitives.ReadUInt64LittleEndian(address.AsSpan(32)) == 0 && BinaryPrimitives.ReadInt16LittleEndian(address.AsSpan(40)) == 0)
                    { ownHandle = true; opened.TrySetResult(); }
                    if (pid == process.Id && address[9] == 9 && BinaryPrimitives.ReadInt32LittleEndian(address.AsSpan(28)) == 0)
                    { ownHandleClosed = true; return; }
                }
            });
            var observing = Task.Run(() => { while (api.Receive(sniff.Value, out var packet, out var address)) {
                if (packet.Length >= 20) { if (packet[9] == 6) Interlocked.Increment(ref loopTcp); if (packet[9] == 17) Interlocked.Increment(ref loopUdp); }
            } });
            var transformations = Task.Run(() => { while (api.Receive(syntheticSniff.Value, out var packet, out var address)) Interlocked.Increment(ref transformedPackets); });
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(ownHandle);
                using var client = new TcpClient(); var accept = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
                await client.ConnectAsync(IPAddress.Loopback, tcpPort, deadline.Token); using var server = await accept;
                byte[] pattern = Enumerable.Range(0, 128).Select(x => (byte)x).ToArray(), received = new byte[128];
                await client.GetStream().WriteAsync(pattern, deadline.Token); await server.GetStream().ReadExactlyAsync(received, deadline.Token); Assert.Equal(pattern, received);
                await server.GetStream().WriteAsync(pattern, deadline.Token); await client.GetStream().ReadExactlyAsync(received, deadline.Token); Assert.Equal(pattern, received);
                using var sender = new UdpClient(); await sender.SendAsync(pattern, new IPEndPoint(IPAddress.Loopback, udpPort), deadline.Token);
                var datagram = await udp.ReceiveAsync(deadline.Token); Assert.Equal(pattern, datagram.Buffer);
                await udp.SendAsync(pattern, datagram.RemoteEndPoint, deadline.Token); Assert.Equal(pattern, (await sender.ReceiveAsync(deadline.Token)).Buffer);
                // A fixed, synthetic IPv4 UDP datagram is injected through the
                // real driver, then the reviewed any-protocol fake rule. The
                // drop guard prevents originals and generated fakes escaping.
                byte[] packet = new byte[32]; packet[0] = 0x45; BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 32);
                packet[8] = 64; packet[9] = 17; new byte[] { 198, 51, 100, 1 }.CopyTo(packet, 12); new byte[] { 203, 0, 113, 77 }.CopyTo(packet, 16);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 55001); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), (ushort)udpPort);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), 12); packet[28] = 1; packet[29] = 2; packet[30] = 3; packet[31] = 4;
                byte[] metadata = new byte[80]; metadata[10] = 2; // Outbound, not Loopback or Impostor.
                Assert.True(api.Checksum(packet, (uint)packet.Length, metadata, 0));
                Assert.True(api.Send(injector.Value, packet, (uint)packet.Length, out uint sent, metadata)); Assert.Equal((uint)packet.Length, sent);
                var until = Stopwatch.StartNew();
                while ((Volatile.Read(ref transformedPackets) < 2 || Volatile.Read(ref loopTcp) < 2 || Volatile.Read(ref loopUdp) < 2) && until.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50, deadline.Token);
                Assert.True(loopTcp >= 2 && loopUdp >= 2, $"Real loopback capture: TCP={loopTcp}, UDP={loopUdp}");
                Assert.True(transformedPackets > 1, $"Reviewed UDP rule produced no extra packets: count={transformedPackets}; {string.Join('\n', logs)}");
                Assert.False(process.HasExited, "Owned engine exited during live test.");
            }
            finally
            {
                if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await output;
                reflect.Shutdown(); sniff.Shutdown(); syntheticSniff.Shutdown();
                await Task.WhenAll(reflection, observing, transformations).WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.True(ownHandleClosed, "Owned WinDivert NETWORK handle did not report closure.");
            Assert.True(process.HasExited);
            Directory.CreateDirectory(Path.Combine(repo, "TestResults"));
            File.WriteAllText(Path.Combine(repo, "TestResults/engine-flowseal-real-capture.txt"),
                $"ACTUAL Windows WinDivert: owned winws NETWORK handle observed; real isolated loopback TCP packets={loopTcp} UDP packets={loopUdp}; bidirectional fixed-pattern reconstruction matched. Contained synthetic reviewed UDP any-protocol rule yielded packets={transformedPackets} from one input, all dropped before NIC. Driver capture/UDP transformation evidence is laboratory-only. Loopback passes unchanged by upstream design; no real TLS/QUIC/STUN, Telegram or ISP bypass validated. No debug/raw packet records retained. Known kernel loss is unmeasured. Owned process exit/handles cleaned up.");
        }
        finally { listener.Stop(); if (Directory.Exists(user)) Directory.Delete(user, true); }
    }

    // Test-only binding to the HASH-VERIFIED, leased absolute installed DLL.
    private sealed class Divert : IDisposable
    {
        private readonly IntPtr library;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr Open([MarshalAs(UnmanagedType.LPStr)] string filter, int layer, short priority, ulong flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool ReceiveDelegate(IntPtr h, [Out] byte[] packet, uint length, out uint received, [Out] byte[] address);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate bool SendDelegate(IntPtr h, byte[] packet, uint length, out uint sent, byte[] address);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate bool ChecksumDelegate(byte[] packet, uint length, byte[] address, ulong flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool Close(IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool ShutdownDelegate(IntPtr h, int how);
        private readonly Open open; private readonly ReceiveDelegate recv; private readonly Close close; private readonly ShutdownDelegate shutdown;
        public readonly SendDelegate Send; public readonly ChecksumDelegate Checksum;
        public Divert(string path) { library = NativeLibrary.Load(path); open = Export<Open>("WinDivertOpen"); recv = Export<ReceiveDelegate>("WinDivertRecv"); close = Export<Close>("WinDivertClose"); shutdown = Export<ShutdownDelegate>("WinDivertShutdown"); Send = Export<SendDelegate>("WinDivertSend"); Checksum = Export<ChecksumDelegate>("WinDivertHelperCalcChecksums"); }
        private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        public Owned Handle(string filter, int layer, short priority, ulong flags) { var h = open(filter, layer, priority, flags); if (h == new IntPtr(-1)) throw new InvalidOperationException("WinDivert test handle failed."); return new(this, h); }
        public bool Receive(IntPtr h, out byte[] packet, out byte[] address) { packet = new byte[65535]; address = new byte[80]; if (!recv(h, packet, (uint)packet.Length, out uint length, address)) return false; Array.Resize(ref packet, (int)length); return true; }
        public sealed class Owned(Divert api, IntPtr h) : IDisposable { public IntPtr Value => h; public void Shutdown() { if (!api.shutdown(h, 1)) throw new InvalidOperationException("WinDivert test receive shutdown failed."); } public void Dispose() => api.close(h); }
        public void Dispose() => NativeLibrary.Free(library);
    }
}
