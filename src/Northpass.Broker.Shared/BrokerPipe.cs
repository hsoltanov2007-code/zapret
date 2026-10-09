using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
namespace Northpass.Broker;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class BrokerPipe
{
    public static string NewId() => Guid.NewGuid().ToString("N");
    public static bool ValidId(string id) => id.Length==32 && id.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Name(string id) => ValidId(id) ? "\\\\.\\pipe\\Northpass.Broker."+id : throw new InvalidDataException("Invalid broker identifier.");
    public static NamedPipeServerStream CreateServer(string id)
    {
        string sid=WindowsIdentity.GetCurrent().User!.Value;
        string sddl="D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x0012019b;;;"+sid+")S:(ML;;NW;;;ME)";
        if(!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl,1,out var descriptor,out _))throw new IOException("Broker pipe security descriptor failed.");
        try
        {
            var attributes=new SecurityAttributes { Length=Marshal.SizeOf<SecurityAttributes>(), Descriptor=descriptor };
            var handle=CreateNamedPipe(Name(id),0x40080003,8,1,65536,65536,0,ref attributes); // overlapped, first instance, reject remote
            if(handle.IsInvalid){handle.Dispose();throw new IOException("Exclusive protected broker pipe could not be created.");}
            return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);
        }
        finally { LocalFree(descriptor); }
    }
    public static async Task<NamedPipeClientStream> ConnectAsync(string id,CancellationToken token)
    {
        for(int attempt=0;attempt<500;attempt++)
        {
            token.ThrowIfCancellationRequested();
            var handle=CreateFile(Name(id),0x00100003,0,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
            if(!handle.IsInvalid)return new NamedPipeClientStream(PipeDirection.InOut,true,true,handle);
            int error=Marshal.GetLastWin32Error();handle.Dispose();
            if(error is not(2 or 231))throw new IOException("Protected broker pipe connection failed: "+error);
            await Task.Delay(20,token);
        }
        throw new TimeoutException("Broker owner pipe unavailable.");
    }
    public static int PeerPid(PipeStream pipe,bool server)
    {
        bool success=server ? GetNamedPipeClientProcessId(pipe.SafePipeHandle,out uint pid) : GetNamedPipeServerProcessId(pipe.SafePipeHandle,out pid);
        if(!success || pid==0 || pid>int.MaxValue)throw new UnauthorizedAccessException("Broker peer PID query failed.");return (int)pid;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll",EntryPoint="ConvertStringSecurityDescriptorToSecurityDescriptorW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text,uint revision,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll",EntryPoint="CreateNamedPipeW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafePipeHandle CreateNamedPipe(string name,uint open,uint mode,uint instances,uint output,uint input,uint timeout,ref SecurityAttributes security);
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafePipeHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
}
