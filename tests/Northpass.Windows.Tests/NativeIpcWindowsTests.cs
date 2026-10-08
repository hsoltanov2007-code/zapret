using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Northpass.Desktop;
using Northpass.Engine.Native;
using Northpass.Models;
using Northpass.Services.Installation;
namespace Northpass.Windows.Tests;

public sealed class NativeIpcWindowsTests
{
    private static string Repository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Northpass.sln"))) return folder.FullName;
        throw new DirectoryNotFoundException();
    }
    private static EngineInstallationManager Manager(HttpClient http)
    {
        using var catalog = NativeCatalog.OpenTrustedManifest();
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-Native-0.2"), http,
            new WindowsInstallationSecurity(), EngineManifest.Parse(catalog), offlinePayload: Path.Combine(Repository(), "dist/native/native-offline.zip"),
            probe: NativeEngine.VerifyInstalledVersionAsync, requireOfflinePayload: true);
    }
    private static ProcessStartInfo Fixture(string mode, string argument)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "fixture/Northpass.TestChild.dll"), mode, argument }) info.ArgumentList.Add(arg);
        return info;
    }
    [Fact]
    public async Task AuthenticatedOwnedChannelMetricsIpv6IntegrityAndConcurrentShutdownAreReal()
    {
        using var http = new HttpClient(); var manager = Manager(http); var installed = await manager.EnsureInstalledAsync();
        await using var engine = new NativeEngine(manager, useNamedPipe: true);
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port; Assert.InRange(port, 49152, 65535);
        await engine.StartAsync(new(installed.ExecutablePath, NativeCatalog.Loopback(port, "udp")));
        byte[] bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        // 64 dedicated loopback datagrams; each complete payload must arrive unchanged.
        for (int i = 0; i < 64; i++)
        {
            var receipt = receiver.ReceiveAsync(); await sender.SendAsync(bytes, new IPEndPoint(IPAddress.IPv6Loopback, port));
            Assert.Equal(bytes, (await receipt.WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
        }
        await Task.Delay(150); var measurements = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => engine.GetPerformanceAsync()));
        Assert.All(measurements, value => { Assert.NotNull(value); Assert.True(value.KernelLossUnknown); Assert.True(value.MemoryBytes > 0); Assert.Equal(0ul, value.KnownDropped); });
        Assert.True(measurements.Last()!.Captured >= 64); Assert.Equal(measurements.Last()!.Captured, measurements.Last()!.Forwarded);
        var pending = engine.GetPerformanceAsync(); var stopping = engine.StopAsync(); await pending; await stopping;
        Assert.Null((await engine.GetStatusAsync()).ProcessId);
        var final = await engine.GetPerformanceAsync(); Assert.True(final!.KernelLossUnknown); Assert.Equal(0ul, final.FatalErrors);
        Directory.CreateDirectory(Path.Combine(Repository(), "TestResults"));
        File.WriteAllText(Path.Combine(Repository(), "TestResults/engine-native-ipc.txt"),
            "Actual authenticated PID/logon-bound named pipe, owned start/stop, concurrent metrics, and 64 unchanged isolated ::1 UDP datagrams passed. " +
            System.Text.Json.JsonSerializer.Serialize(final) + " Kernel loss remains unobservable; this is not internet/bypass or throughput certification.");
        // Actual owner death on the new control path, not a mocked cancellation.
        using var parent = Process.Start(Fixture("native-ipc-parent", installed.ExecutablePath))!;
        string? line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.StartsWith("native-child-pid=", line);
        int childPid = int.Parse(line![17..]); await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, parent.ExitCode);
        for (int i = 0; i < 50; i++)
        {
            try { using var child = Process.GetProcessById(childPid); if (child.HasExited) return; }
            catch (ArgumentException) { return; }
            await Task.Delay(100);
        }
        using var orphan = Process.GetProcessById(childPid); orphan.Kill(true); Assert.Fail("Authenticated worker survived parent death.");
    }
    [Fact]
    public async Task WrongPeerWrongTokenReplayAndPipeSquattingAreRejectedBeforeUnscopedCapture()
    {
        using var http = new HttpClient(); var manager = Manager(http); var installed = await manager.EnsureInstalledAsync();
        string id = NativePipeClient.NewIdentifier(), secret = new string('a', 64);
        await using var lease = await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath);
        var info = NativeEngine.CreateStartInfo(new(installed.ExecutablePath, NativeCatalog.Idle()), id);
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(secret); await process.StandardInput.FlushAsync();
            using (var intruder = Process.Start(Fixture("native-pipe-intruder", id))!)
            {
                var output = intruder.StandardOutput.ReadToEndAsync(); var error = intruder.StandardError.ReadToEndAsync();
                await intruder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(intruder.ExitCode == 0, await error); Assert.Contains("untrusted-peer-rejected", await output);
            }
            // Correct process, wrong private nonce: rejected without starting driver.
            using (var wrong = await OpenAsync(id))
            {
                await WriteAsync(wrong, "AUTH 2 " + new string('b', 64));
                await Assert.ThrowsAnyAsync<IOException>(() => ReadAsync(wrong));
            }
            using var pipe = await OpenAsync(id); await WriteAsync(pipe, "AUTH 2 " + secret); Assert.Equal("AUTH_OK 2", await ReadAsync(pipe));
            Assert.Equal("NORTHPASS_READY protocol=1", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await WriteAsync(pipe, "METRICS 2 1"); var metrics = NativeMetrics.Parse(await ReadAsync(pipe)); Assert.True(metrics.AuthenticationFailures >= 2);
            Assert.Equal(0ul, metrics.Captured); // idle false filter; no ordinary traffic
            await WriteAsync(pipe, "PING 2 2"); Assert.Equal("OK 2 2", await ReadAsync(pipe));
            await WriteAsync(pipe, "STOP 2 2"); // replay must be a control failure, never accepted as a successful Stop
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.NotEqual(0, process.ExitCode); Assert.Contains("NORTHPASS_ERROR", await errors);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        string occupied = NativePipeClient.NewIdentifier();
        using var squatter = new NamedPipeServerStream("Northpass.Native." + occupied, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var blocked = NativeEngine.CreateStartInfo(new(installed.ExecutablePath, NativeCatalog.Idle()), occupied);
        blocked.RedirectStandardInput = blocked.RedirectStandardOutput = blocked.RedirectStandardError = true;
        using var rejected = Process.Start(blocked)!; var rejection = rejected.StandardError.ReadToEndAsync();
        await rejected.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.NotEqual(0, rejected.ExitCode); Assert.Contains("IPC creation", await rejection);
        File.WriteAllText(Path.Combine(Repository(), "TestResults/engine-native-ipc-security.txt"),
            "Actual Windows same-logon wrong-PID client, correct-PID wrong nonce, replayed command and preclaimed pipe rejected. Only original parent authenticated; no unscoped capture permitted.");
    }
    private static async Task<NamedPipeClientStream> OpenAsync(string id)
    {
        for (int i = 0; i < 400; i++)
        {
            var handle = CreateFile("\\\\.\\pipe\\Northpass.Native." + id, 0x00100003, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (!handle.IsInvalid) return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
            handle.Dispose(); await Task.Delay(20);
        }
        throw new IOException("Test pipe unavailable.");
    }
    private static async Task WriteAsync(Stream pipe, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text), frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length); bytes.CopyTo(frame, 4);
        await pipe.WriteAsync(frame).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task<string> ReadAsync(Stream pipe)
    {
        byte[] header = new byte[4]; await pipe.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header); Assert.InRange(length, 1, 2048);
        byte[] bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(3)); return Encoding.ASCII.GetString(bytes);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
