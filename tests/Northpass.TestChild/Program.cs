using System.Diagnostics;
using System.Text.Json;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Text;

// A harmless, real child-process fixture. This is not the production engine or a Windows driver.
string mode = args.FirstOrDefault() ?? "wait";
if (mode == "exit") { Console.Error.WriteLine("fixture startup error"); return 17; }
if (mode is "protocol" or "protocol-stderr" or "protocol-hang" or "protocol-error")
{
    if (mode == "protocol-stderr") Console.Error.WriteLine("NORTHPASS_READY protocol=1");
    if (mode == "protocol-hang") { await Task.Delay(TimeSpan.FromMinutes(2)); return 0; }
    if (mode is "protocol" or "protocol-error") Console.WriteLine("NORTHPASS_READY protocol=1");
    if (await Console.In.ReadLineAsync() == "STOP") { Console.WriteLine("fixture graceful stop"); return mode == "protocol-error" ? 27 : 0; }
    return 18;
}
if (mode == "native-pipe-intruder")
{
    using var pipe = await PipeFixture.OpenAsync(args[1]);
    try
    {
        await PipeFixture.WriteAsync(pipe, "AUTH 2 " + new string('0', 64));
        _ = await PipeFixture.ReadAsync(pipe); return 31; // untrusted process must never authenticate
    }
    catch (IOException) { Console.WriteLine("untrusted-peer-rejected"); return 0; }
}
if (mode is "native-parent" or "native-ipc-parent")
{
    var info = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "--mode", "idle", "--parent-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }) info.ArgumentList.Add(argument);
    string id = Guid.NewGuid().ToString("N");
    if (mode == "native-parent") info.ArgumentList.Add("--stdio-control");
    else { info.ArgumentList.Add("--pipe-id"); info.ArgumentList.Add(id); }
    using var child = Process.Start(info)!;
    if (mode == "native-ipc-parent")
    {
        string secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await child.StandardInput.WriteLineAsync(secret); await child.StandardInput.FlushAsync();
        var pipe = await PipeFixture.OpenAsync(id);
        await PipeFixture.WriteAsync(pipe, "AUTH 2 " + secret);
        if (await PipeFixture.ReadAsync(pipe) != "AUTH_OK 2") throw new IOException("Fixture IPC authentication failed.");
        GC.KeepAlive(pipe); // abrupt owner exit below; no orderly disconnect
    }
    var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (ready != "NORTHPASS_READY protocol=1") { if (!child.HasExited) child.Kill(); throw new InvalidOperationException("Native parent fixture initialization failed: " + await child.StandardError.ReadToEndAsync()); }
    Console.WriteLine("native-child-pid=" + child.Id);
    // Exit the real owner abruptly while the pipe/driver session is active.
    Environment.Exit(0);
}
Console.WriteLine("fixture ready");
Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToArray()));
Console.Error.WriteLine("fixture stderr");
if (mode == "crash")
{
    // Host signals only after StartAsync has returned; runner scheduling must not
    // turn an unexpected-exit test into an immediate-startup-exit test.
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!File.Exists(args[1]) && DateTime.UtcNow < deadline) await Task.Delay(20);
    return File.Exists(args[1]) ? 23 : 24;
}
if (mode == "spawn")
{
    var child = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    child.ArgumentList.Add(typeof(Program).Assembly.Location);
    child.ArgumentList.Add("wait");
    using var process = Process.Start(child)!;
    Console.WriteLine("child-pid=" + process.Id);
}
await Task.Delay(TimeSpan.FromMinutes(2));
return 0;

static class PipeFixture
{
    public static async Task<NamedPipeClientStream> OpenAsync(string id)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            var handle = CreateFile("\\\\.\\pipe\\Northpass.Native." + id, 0x00100003, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (!handle.IsInvalid) return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
            handle.Dispose(); await Task.Delay(20);
        }
        throw new IOException("Fixture pipe unavailable.");
    }
    public static async Task WriteAsync(Stream pipe, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text), frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length); bytes.CopyTo(frame, 4);
        await pipe.WriteAsync(frame).AsTask().WaitAsync(TimeSpan.FromSeconds(3)); await pipe.FlushAsync();
    }
    public static async Task<string> ReadAsync(Stream pipe)
    {
        byte[] header = new byte[4]; await pipe.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header); if (length is < 1 or > 2048) throw new IOException("Fixture frame rejected.");
        byte[] bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(3)); return Encoding.ASCII.GetString(bytes);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
