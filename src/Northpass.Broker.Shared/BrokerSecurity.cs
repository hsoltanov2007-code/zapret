using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Northpass.Desktop;
using Northpass.Services.Installation;
namespace Northpass.Broker;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class BrokerSecurity
{
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory,"broker","Northpass.Broker.exe");
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void ValidateProtectedApplication(string root)
    {
        string full=Path.GetFullPath(root), program=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!full.StartsWith(program+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The helper requires a protected Program Files installation.");
        var security=new WindowsInstallationSecurity(); SafeArchive.NoLinks(full);
        security.ValidateDirectory(full);
        void Walk(string directory) {
            foreach(string path in Directory.EnumerateFileSystemEntries(directory))
                if (Directory.Exists(path)) { security.ValidateDirectory(path); Walk(path); } else security.ValidateFile(path);
        }
        Walk(full);
    }
    public static List<FileStream> VerifyHelper()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ValidateProtectedApplication(AppContext.BaseDirectory);
        using var manifest=System.Reflection.Assembly.GetEntryAssembly()?.GetManifestResourceStream("Northpass.Broker.Manifest") ?? throw new InvalidDataException("Trusted helper catalog is missing.");
        var files=JsonSerializer.Deserialize<Dictionary<string,string>>(manifest) ?? throw new InvalidDataException("Invalid helper catalog.");
        var leases=new List<FileStream>();
        try
        {
            var actual=Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"broker"),"*",SearchOption.AllDirectories);
            if(actual.Length!=files.Count)throw new InvalidDataException("Unexpected helper component.");
            foreach (string path in actual)
            {
                string name=Path.GetRelativePath(AppContext.BaseDirectory,path).Replace('\\','/');
                if(!files.TryGetValue(name,out string? hash))throw new InvalidDataException("Unknown helper component.");
                var lease=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read); leases.Add(lease);
                if(!Convert.ToHexString(SHA256.HashData(lease)).Equals(hash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Helper component integrity check failed.");
            }
            return leases;
        }
        catch { foreach(var lease in leases)lease.Dispose();throw; }
    }
    public static void ValidatePeer(Process process,string expectedImage,long expectedStart,bool requireAdmin)
    {
        if(process.HasExited || process.StartTime.ToUniversalTime().ToFileTimeUtc()!=expectedStart)throw new UnauthorizedAccessException("Broker peer process identity changed.");
        char[] buffer=new char[32768];uint length=(uint)buffer.Length;
        using var queried=OpenProcess(0x1000,false,process.Id);
        if(queried.IsInvalid || !QueryFullProcessImageName(queried,0,buffer,ref length) || !Path.GetFullPath(new string(buffer,0,(int)length)).Equals(Path.GetFullPath(expectedImage),StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Broker peer executable substitution rejected.");
        if(!OpenProcessToken(queried,8,out var token))throw new UnauthorizedAccessException("Broker peer token unavailable.");
        using(token)
        using(var identity=new WindowsIdentity(token.DangerousGetHandle()))
        using(var current=WindowsIdentity.GetCurrent())
        {
            if(identity.User!=current.User || Logon(token)!=Logon(current.AccessToken))throw new UnauthorizedAccessException("Broker peer user/logon mismatch.");
            if(requireAdmin && !IsElevatedAdministrator(token))throw new UnauthorizedAccessException("Broker peer is not elevated.");
        }
    }
    private static bool IsElevatedAdministrator(SafeAccessTokenHandle token)
    {
        // Query only. WindowsPrincipal.IsInRole duplicates a foreign primary
        // token and would require TOKEN_DUPLICATE across the integrity boundary.
        byte[] elevation=new byte[4];
        if(!GetTokenInformation(token,20,elevation,4,out _) || BitConverter.ToInt32(elevation)!=1)return false;
        _=GetTokenBuffer(token,2,IntPtr.Zero,0,out int size);
        if(size is <8 or >65536)throw new UnauthorizedAccessException("Invalid peer group information size.");
        IntPtr buffer=Marshal.AllocHGlobal(size);
        try {
            if(!GetTokenBuffer(token,2,buffer,size,out _))throw new UnauthorizedAccessException("Peer group query failed.");
            int count=Marshal.ReadInt32(buffer);int stride=IntPtr.Size==8?16:8,offset=IntPtr.Size;
            if(count<0 || count>(size-offset)/stride)throw new UnauthorizedAccessException("Peer group bounds rejected.");
            for(int i=0;i<count;i++) {
                IntPtr sid=Marshal.ReadIntPtr(buffer,offset+i*stride);int flags=Marshal.ReadInt32(buffer,offset+i*stride+IntPtr.Size);
                if((flags&4)!=0 && (flags&16)==0 && new SecurityIdentifier(sid).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))return true;
            }
            return false;
        } finally {Marshal.FreeHGlobal(buffer);}
    }
    [DllImport("advapi32.dll",EntryPoint="GetTokenInformation",SetLastError=true)]private static extern bool GetTokenBuffer(SafeAccessTokenHandle token,int kind,IntPtr buffer,int size,out int length);
    private static string Logon(SafeAccessTokenHandle token)
    {
        byte[] buffer=new byte[128];
        byte[] session=new byte[4];
        if(!GetTokenInformation(token,10,buffer,buffer.Length,out _) || !GetTokenInformation(token,12,session,4,out _))throw new UnauthorizedAccessException("Token authorization query failed.");
        // TOKEN_STATISTICS: AuthenticationId at offset 8, invariant across a UAC split token.
        return Convert.ToHexString(buffer.AsSpan(8,8))+Convert.ToHexString(session);
    }
    public static SafeProcessHandle ObserveProcess(Process process)
    {
        var handle=OpenProcess(0x101000,false,process.Id); // synchronize + query limited; no write/control access
        if(handle.IsInvalid){handle.Dispose();throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
        return handle;
    }
    public static bool HasExited(SafeProcessHandle handle)
    {
        uint state=WaitForSingleObject(handle,0);
        return state switch {0=>true,258=>false,_=>throw new IOException("Owned broker process observation failed.")};
    }
    public static async Task<bool> WaitForExitAsync(SafeProcessHandle handle,TimeSpan timeout)
    {
        var clock=Stopwatch.StartNew();while(!HasExited(handle)) {if(clock.Elapsed>=timeout)return false;await Task.Delay(25).ConfigureAwait(false);}return true;
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint timeout);
    public static void ValidateWorker(Process worker,Process bootstrap)
    {
        if(bootstrap.HasExited || worker.StartTime<bootstrap.StartTime)throw new UnauthorizedAccessException("Broker bootstrap ownership changed.");
        using var snapshot=CreateToolhelp32Snapshot(2,0);
        var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>()};bool matched=false;
        if(Process32First(snapshot,ref entry))do{if(entry.Pid==worker.Id){matched=entry.ParentPid==bootstrap.Id;break;}}while(Process32Next(snapshot,ref entry));
        if(!matched)throw new UnauthorizedAccessException("Broker worker is not the owned bootstrap child.");
        ValidatePeer(worker,Path.Combine(AppContext.BaseDirectory,"broker","Northpass.Broker.Worker.exe"),worker.StartTime.ToUniversalTime().ToFileTimeUtc(),true);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ProcessEntry
    {public uint Size,Usage,Pid;public UIntPtr Heap;public uint Module,Threads,ParentPid;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",EntryPoint="Process32FirstW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool Process32First(SafeFileHandle snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",EntryPoint="Process32NextW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool Process32Next(SafeFileHandle snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,char[] name,ref uint length);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,byte[] data,int length,out int returned);
}
