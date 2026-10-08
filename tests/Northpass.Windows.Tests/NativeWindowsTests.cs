using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Native;
using Northpass.Models;
using Northpass.Services;
using Northpass.Services.Installation;
namespace Northpass.Windows.Tests;

public sealed class NativeWindowsTests
{
    private static string Repository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Northpass.sln"))) return folder.FullName;
        throw new DirectoryNotFoundException("Repository checkout required.");
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; throw new InvalidOperationException("Native installation attempted a network download."); }
    }
    private static bool Alive(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static string Stats(IEnumerable<string> logs) => logs.Last(l => l.StartsWith("NORTHPASS_STATS "));
    private static long Counter(string stats, string key) => long.Parse(stats.Split(' ').Single(p => p.StartsWith(key + "="))[(key.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    private static void Evidence(string repo, string name, string text)
    { Directory.CreateDirectory(Path.Combine(repo, "TestResults")); File.WriteAllText(Path.Combine(repo, "TestResults", name + ".txt"), text); }

    [Fact]
    public async Task NativeOfflineOwnershipLoopbackPassThroughAndParentDeathAreRealWindowsChecks()
    {
        string repo = Repository(), root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-Native");
        using var catalog = NativeCatalog.OpenTrustedManifest(); var manifest = EngineManifest.Parse(catalog);
        using var handler = new NoNetwork(); using var http = new HttpClient(handler); var security = new WindowsInstallationSecurity();
        var manager = new EngineInstallationManager(root, http, security, manifest,
            offlinePayload: Path.Combine(repo, "dist/native/native-offline.zip"), probe: NativeEngine.VerifyInstalledVersionAsync, requireOfflinePayload: true);
        var installed = await manager.EnsureInstalledAsync();
        Assert.Equal(installed, await manager.DetectAsync()); Assert.Equal(installed, await manager.EnsureInstalledAsync()); Assert.Equal(0, handler.Requests);
        foreach (var asset in manifest.Components) security.ValidateFile(Path.Combine(root, manifest.Revision, asset.Path));
        var dll = new FileInfo(Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!, "WinDivert.dll"));
        var acl = dll.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Allow));
        dll.SetAccessControl(acl);
        try { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.DetectAsync()); }
        finally { security.ProtectFile(dll.FullName); }
        // Direct native entry also refuses an unsafe runtime ACL, independently of C#.
        var exeAcl = new FileInfo(installed.ExecutablePath).GetAccessControl();
        exeAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Allow));
        new FileInfo(installed.ExecutablePath).SetAccessControl(exeAcl);
        try
        {
            var info = new ProcessStartInfo(installed.ExecutablePath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in new[] { "--mode", "idle", "--check" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, process.ExitCode); Assert.Contains("unsafe", await error);
        }
        finally { security.ProtectFile(installed.ExecutablePath); }
        var logs = new ConcurrentQueue<string>();
        await using var engine = new NativeEngine(manager); engine.LogReceived += logs.Enqueue;
        var idle = new EngineConfiguration(installed.ExecutablePath, NativeCatalog.Idle());
        await engine.StartAsync(idle);
        int pid = (await engine.GetStatusAsync()).ProcessId!.Value;
        Assert.Equal(EngineState.Active, (await engine.GetStatusAsync()).State); Assert.Contains("NORTHPASS_READY protocol=1", logs);
        Assert.Throws<IOException>(() => File.Open(dll.FullName, FileMode.Open, FileAccess.Write, FileShare.Read));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(idle));
        await using (var second = new NativeEngine(manager)) await Assert.ThrowsAsync<EngineConflictException>(() => second.StartAsync(idle));
        Assert.True(Alive(pid)); await engine.StopAsync(); Assert.False(Alive(pid));
        Assert.Equal(0, Counter(Stats(logs), "packets")); Assert.Contains("NORTHPASS_STOPPED protocol=1", logs);
        Assert.DoesNotContain(logs, l => l.Contains("Graceful native shutdown failed"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StartAsync(idle, cancelled.Token));
        Assert.Null((await engine.GetStatusAsync()).ProcessId);

        // Only this test's reserved IPv4 UDP socket is intercepted; ordinary traffic is excluded.
        using (var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port; Assert.InRange(port, 49152, 65535);
            logs.Clear(); await engine.StartAsync(new(installed.ExecutablePath, NativeCatalog.Loopback(port, "udp")));
            byte[] bytes = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
            var reception = receiver.ReceiveAsync(); await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port));
            var result = await reception.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(bytes, result.Buffer);
            await receiver.SendAsync(result.Buffer, result.RemoteEndPoint);
            Assert.Equal(bytes, (await sender.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
            await engine.StopAsync(); string stats = Stats(logs);
            Assert.True(Counter(stats, "udp") >= 2, stats); Assert.Equal(Counter(stats, "packets"), Counter(stats, "forwarded"));
            Evidence(repo, "engine-native-udp", "Original UDP bytes arrived unchanged in both directions on a dedicated 127.0.0.1 socket. " + stats);
        }
        // A complete synthetic ClientHello framing record is observed, never decoded or modified.
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; Assert.InRange(port, 49152, 65535);
            logs.Clear(); await engine.StartAsync(new(installed.ExecutablePath, NativeCatalog.Loopback(port, "tcp")));
            var accept = listener.AcceptTcpClientAsync(); using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
            using var server = await accept.WaitAsync(TimeSpan.FromSeconds(5)); server.NoDelay = true;
            byte[] bytes = new byte[43]; bytes[0] = 22; bytes[1] = 3; bytes[2] = 1; bytes[4] = 38; bytes[5] = 1; bytes[8] = 34;
            byte[] received = new byte[bytes.Length];
            await client.GetStream().WriteAsync(bytes); await server.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(bytes, received);
            await server.GetStream().WriteAsync(received); await client.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(bytes, received);
            server.Close(); client.Close(); await engine.StopAsync(); string stats = Stats(logs);
            Assert.True(Counter(stats, "tcp") >= 3 && Counter(stats, "tls") >= 1 && Counter(stats, "flows") >= 1, stats);
            Assert.Equal(Counter(stats, "packets"), Counter(stats, "forwarded"));
            Evidence(repo, "engine-native-tcp", "Dedicated 127.0.0.1 TCP stream echoed unchanged bytes; synthetic TLS framing classified, bounded flow tracked. " + stats + " This is not an authenticated TLS session or DPI bypass.");
        }
        finally { listener.Stop(); await engine.StopAsync(); }
        // Real replaceable controller composition, not a standalone unregistered child.
        var registry = new EngineRegistry(); registry.Register(NativeEngine.Metadata, () => new NativeEngine(manager));
        await using (var controller = new EngineController(registry))
        {
            await controller.ConnectAsync(idle, autoRecover: false); Assert.Equal(EngineState.Active, (await controller.GetStatusAsync()).State);
            await controller.DisconnectAsync(); Assert.Equal(EngineState.Disconnected, (await controller.GetStatusAsync()).State);
        }
        var parentInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "fixture/Northpass.TestChild.dll"), "native-parent", installed.ExecutablePath }) parentInfo.ArgumentList.Add(arg);
        int orphan;
        await using (var lease = await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath))
        {
            using var parent = Process.Start(parentInfo)!;
            string? line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.StartsWith("native-child-pid=", line); orphan = int.Parse(line![17..], System.Globalization.CultureInfo.InvariantCulture);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, parent.ExitCode);
            for (int attempt = 0; attempt < 50 && Alive(orphan); attempt++) await Task.Delay(100);
            if (Alive(orphan)) { using var leftover = Process.GetProcessById(orphan); leftover.Kill(true); Assert.Fail("Native process survived real parent termination."); }
        }
        Assert.False(Alive(orphan)); Assert.NotNull(await manager.DetectAsync());
        Evidence(repo, "engine-native-lifecycle", "Actual NorthpassCore 0.1.0 installed/reused offline with immutable component hashes and protected ACLs. Unsafe ACL rejected by managed/native checks. Real idle driver readiness, duplicate/collision rejection, file leases, graceful STOP/drain, cancellation, IDpiEngine controller and parent-death cleanup passed. No ordinary internet traffic captured, security settings changed, DPI bypass or Russian ISP effectiveness tested.");
    }
}
