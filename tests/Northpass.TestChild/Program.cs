using System.Diagnostics;
using System.Text.Json;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Text;

// A harmless, real child-process fixture. This is not the production engine or a Windows driver.
string mode = args.FirstOrDefault() ?? "wait";
if (mode == "seal-install") {
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    new Northpass.Desktop.WindowsInstallationSecurity().ProtectDirectory(args[1]);return 0;
}
if (mode == "medium-launch") return MediumLauncher.Run(args[1], args[2]);
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
    NamedPipeClientStream? controlPipe = null;
    if (mode == "native-ipc-parent")
    {
        string secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await child.StandardInput.WriteLineAsync(secret); await child.StandardInput.FlushAsync();
        controlPipe = await PipeFixture.OpenAsync(id);
        await PipeFixture.WriteAsync(controlPipe, "AUTH 2 " + secret);
        if (await PipeFixture.ReadAsync(controlPipe) != "AUTH_OK 2") throw new IOException("Fixture IPC authentication failed.");
    }
    var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (ready != "NORTHPASS_READY protocol=1") { if (!child.HasExited) child.Kill(); throw new InvalidOperationException("Native parent fixture initialization failed: " + await child.StandardError.ReadToEndAsync()); }
    Console.WriteLine("native-child-pid=" + child.Id);
    GC.KeepAlive(controlPipe); // preserve control ownership until abrupt exit, not finalizer-driven closure
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
        int error = 0;
        for (int attempt = 0; attempt < 400; attempt++)
        {
            var handle = CreateFile("\\\\.\\pipe\\Northpass.Native." + id, 0x00100003, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (!handle.IsInvalid) return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
            error = Marshal.GetLastWin32Error(); handle.Dispose(); await Task.Delay(20);
        }
        throw new IOException("Fixture pipe unavailable, Win32 error " + error);
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

// Launch the actual published UI with a restricted medium-integrity token.
// This fixture supplies a real Windows token, not a mocked administrator check.
static class MediumLauncher
{
    public static int Run(string image, string arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        IntPtr original=IntPtr.Zero, restricted=IntPtr.Zero, admin=IntPtr.Zero, medium=IntPtr.Zero;
        try
        {
            Check(OpenProcessToken(GetCurrentProcess(),0xf01ff,out original));
            Check(ConvertStringSidToSid("S-1-5-32-544",out admin));
            var deny = new SidAttributes { Sid=admin };
            Check(CreateRestrictedToken(original,1,1,ref deny,0,IntPtr.Zero,0,IntPtr.Zero,out restricted));
            Check(ConvertStringSidToSid("S-1-16-8192",out medium));
            var label = new SidAttributes { Sid=medium, Attributes=0x20 };
            Check(SetTokenInformation(restricted,25,ref label,Marshal.SizeOf<SidAttributes>()+GetLengthSid(medium)));
            var startup=new StartupInfo { Size=Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder("\""+image+"\" "+arguments);
            Check(CreateProcessWithTokenW(restricted,1,image,command,0,IntPtr.Zero,Path.GetDirectoryName(image),ref startup,out var process));
            try
            {
                if(WaitForSingleObject(process.Process,120000)!=0)throw new TimeoutException("Actual medium UI timed out.");
                Check(GetExitCodeProcess(process.Process,out uint code));return checked((int)code);
            }
            finally { CloseHandle(process.Thread);CloseHandle(process.Process); }
        }
        finally { if(original!=IntPtr.Zero)CloseHandle(original);if(restricted!=IntPtr.Zero)CloseHandle(restricted);if(admin!=IntPtr.Zero)LocalFree(admin);if(medium!=IntPtr.Zero)LocalFree(medium); }
    }
    private static void Check(bool success){if(!success)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
    [StructLayout(LayoutKind.Sequential)] struct SidAttributes { public IntPtr Sid;public uint Attributes; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo {
        public int Size;public string? Reserved;public string? Desktop;public string? Title;
        public uint X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;public ushort ShowWindow,Reserved2Size;
        public IntPtr Reserved2,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo {public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr handle,out uint code);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertStringSidToSid(string text,out IntPtr sid);
    [DllImport("advapi32.dll")] static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool CreateRestrictedToken(IntPtr token,uint flags,uint disabled,ref SidAttributes sids,uint removed,IntPtr privileges,uint restricted,IntPtr restrictedSids,out IntPtr result);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetTokenInformation(IntPtr token,int kind,ref SidAttributes data,int length);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessWithTokenW(IntPtr token,uint flags,string application,StringBuilder command,uint creation,IntPtr environment,string? directory,ref StartupInfo startup,out ProcessInfo process);
}
