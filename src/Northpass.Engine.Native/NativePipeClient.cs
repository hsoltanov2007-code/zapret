using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace Northpass.Engine.Native;

// Internal preview. Peer PID verification is mandatory in both directions; a token
// travels only through inherited private stdin, never command lines or log messages.
public sealed class NativePipeClient : IAsyncDisposable
{
    public static string NewIdentifier() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public static bool ValidIdentifier(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private uint _sequence;
    public async Task ConnectAsync(Process ownedChild, string id, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { await ConnectCoreAsync(ownedChild, id, token); }
        finally { _gate.Release(); }
    }
    private async Task ConnectCoreAsync(Process ownedChild, string id, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!ValidIdentifier(id) || _pipe is not null) throw new InvalidOperationException("Invalid IPC initialization.");
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        string secret = Convert.ToHexString(secretBytes).ToLowerInvariant(); CryptographicOperations.ZeroMemory(secretBytes);
        await ownedChild.StandardInput.WriteLineAsync(secret.AsMemory(), token); await ownedChild.StandardInput.FlushAsync(token);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (ownedChild.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new IOException("Owned network component did not create its control channel.");
            // Specific rights exclude the right to create another pipe server instance.
            var handle = CreateFile("\\\\.\\pipe\\Northpass.Native." + id, 0x00100003, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                if (!GetNamedPipeServerProcessId(handle, out uint pid) || pid != ownedChild.Id || ownedChild.HasExited)
                { handle.Dispose(); throw new UnauthorizedAccessException("IPC server is not the owned network component."); }
                _pipe = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle); break;
            }
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is not (2 or 231)) throw new IOException("Protected IPC connection failed.", new Win32Exception(error));
            await Task.Delay(20, token);
        }
        try
        {
            await WriteFrameAsync("AUTH 2 " + secret, token);
            if (await ReadFrameAsync(token) != "AUTH_OK 2") throw new UnauthorizedAccessException("IPC authentication rejected.");
            ownedChild.StandardInput.Close(); _sequence = 0;
        }
        catch { _pipe.Dispose(); _pipe = null; throw; }
    }
    public async Task<string> RequestAsync(string command, CancellationToken token = default)
    {
        if (command is not ("PING" or "METRICS" or "STOP")) throw new InvalidDataException("IPC command is not permitted.");
        await _gate.WaitAsync(token);
        try
        {
            if (_pipe is null || _sequence == uint.MaxValue) throw new IOException("IPC channel is unavailable.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            string sequence = (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await WriteFrameAsync(command + " 2 " + sequence, timeout.Token);
            var response = await ReadFrameAsync(timeout.Token);
            if (command != "METRICS" && response != "OK 2 " + sequence) throw new InvalidDataException("IPC response mismatch.");
            if (command == "METRICS") _ = NativeMetrics.Parse(response);
            return response;
        }
        catch { _pipe?.Dispose(); _pipe = null; throw; } // partial framing/timeout cannot be reused
        finally { _gate.Release(); }
    }
    private async Task WriteFrameAsync(string message, CancellationToken token)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(message); byte[] frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length); bytes.CopyTo(frame, 4);
        try { await _pipe!.WriteAsync(frame, token); await _pipe.FlushAsync(token); }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(frame); }
    }
    private async Task<string> ReadFrameAsync(CancellationToken token)
    {
        byte[] header = new byte[4]; await _pipe!.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 2048) throw new InvalidDataException("IPC response length rejected.");
        byte[] bytes = new byte[length]; await _pipe.ReadExactlyAsync(bytes, token);
        if (bytes.Any(b => b is < 32 or > 126)) throw new InvalidDataException("IPC response encoding rejected.");
        return Encoding.ASCII.GetString(bytes);
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _pipe?.Dispose(); _pipe = null; } finally { _gate.Release(); }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
