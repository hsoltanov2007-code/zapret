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
    // All peer queries use one held process handle. TOKEN_QUERY is sufficient;
    // neither PROCESS_ALL_ACCESS nor TOKEN_DUPLICATE is requested.
    public static BrokerAuthorizationMode ValidatePeer(Process process,string expectedImage,long expectedStart,bool requireAdmin)
    {
        using var queried=OpenProcess(0x101000,false,process.Id);
        if(queried.IsInvalid)throw Api(BrokerSecurityCheck.ProcessOpen);
        uint state=WaitForSingleObject(queried,0);
        if(state==0)throw Rejected(BrokerSecurityCheck.ProcessAlive,BrokerSecurityOutcome.ProcessExited);
        if(state!=258)throw Api(BrokerSecurityCheck.ProcessAlive);
        if(!GetProcessTimes(queried,out long created,out _,out _,out _))throw Api(BrokerSecurityCheck.CreationTimeQuery);
        if(created!=expectedStart)throw Rejected(BrokerSecurityCheck.CreationTime);
        ValidateImage(queried,expectedImage);
        if(!OpenProcessToken(queried,8,out var token))throw Api(BrokerSecurityCheck.PeerTokenOpen);
        using(token)
        using(var currentProcess=Process.GetCurrentProcess())
        using(var currentHandle=ObserveProcess(currentProcess))
        {
            if(!OpenProcessToken(currentHandle,8,out var current))throw Api(BrokerSecurityCheck.CurrentTokenOpen);
            using(current)
            {
                // Only primary tokens obtained from these held process handles
                // can authorize a peer. Reopen after validation to detect replacement.
                RequirePrimary(token);RequirePrimary(current);
                ulong peerId=TokenId(token),currentId=TokenId(current);
                var mode=ValidateTokenIdentity(token,current,requireAdmin);
                CheckTokenUnchanged(queried,peerId);CheckTokenUnchanged(currentHandle,currentId);
                if(HasExited(queried))throw Rejected(BrokerSecurityCheck.ProcessAlive,BrokerSecurityOutcome.ProcessExited);
                return mode;
            }
        }
    }
    private static BrokerSecurityException Api(BrokerSecurityCheck check) =>
        new(new(check,BrokerSecurityOutcome.ApiFailure,Marshal.GetLastWin32Error()));
    private static BrokerSecurityException Rejected(BrokerSecurityCheck check,BrokerSecurityOutcome outcome=BrokerSecurityOutcome.IdentityMismatch) =>
        new(new(check,outcome,unchecked((int)0x80070005)));
    private static void ValidateImage(SafeProcessHandle process,string expectedImage)
    {
        char[] buffer=new char[32768];uint length=(uint)buffer.Length;
        if(!QueryFullProcessImageName(process,0,buffer,ref length))throw Api(BrokerSecurityCheck.ImageQuery);
        if(length==0 || length>buffer.Length)throw Rejected(BrokerSecurityCheck.ImageQuery,BrokerSecurityOutcome.InvalidData);
        if(!Path.GetFullPath(new string(buffer,0,(int)length)).Equals(Path.GetFullPath(expectedImage),StringComparison.OrdinalIgnoreCase))
            throw Rejected(BrokerSecurityCheck.ImagePath);
    }
    // Internal for actual Windows token regression tests, never an IPC surface.
    internal static BrokerAuthorizationMode ValidateTokenIdentity(SafeAccessTokenHandle peer,SafeAccessTokenHandle current,bool requireAdmin)
    {
        var peerIdentity=ReadIdentity(peer,BrokerSecurityCheck.PeerUserQuery,BrokerSecurityCheck.PeerStatisticsQuery,BrokerSecurityCheck.PeerSessionQuery);
        var currentIdentity=ReadIdentity(current,BrokerSecurityCheck.CurrentUserQuery,BrokerSecurityCheck.CurrentStatisticsQuery,BrokerSecurityCheck.CurrentSessionQuery);
        var mode=BrokerIdentityPolicy.Validate(peerIdentity,currentIdentity,requireAdmin,()=>InspectLinked(peer,current));
        if(requireAdmin)
        {
            if(IntValue(peer,20,BrokerSecurityCheck.ElevationQuery)!=1)throw Rejected(BrokerSecurityCheck.Elevated);
            if(!IsAdministratorGroupEnabled(peer))throw Rejected(BrokerSecurityCheck.AdministratorGroup);
        }
        return mode;
    }
    private static BrokerTokenIdentity ReadIdentity(SafeAccessTokenHandle token,BrokerSecurityCheck user,BrokerSecurityCheck statistics,BrokerSecurityCheck session)=>
        new(User(token,user).Value,AuthenticationId(token,statistics),IntValue(token,12,session));
    private static BrokerTokenSnapshot Snapshot(SafeAccessTokenHandle token,BrokerSecurityCheck check)
    {
        var identity=ReadIdentity(token,check,check,check);
        byte[] stats=Query(token,10,56,check);
        int elevated=IntValue(token,20,check);
        if(elevated is not(0 or 1))throw Rejected(check,BrokerSecurityOutcome.InvalidData);
        return new(identity,BitConverter.ToUInt64(stats),BitConverter.ToInt32(stats,24),
            IntValue(token,18,check),elevated==1,IsAdministratorGroupEnabled(token));
    }
    private static void RequirePrimary(SafeAccessTokenHandle token)
    {
        if(IntValue(token,8,BrokerSecurityCheck.TokenTypeQuery)!=1)throw Rejected(BrokerSecurityCheck.TokenTypeQuery);
    }
    private static ulong TokenId(SafeAccessTokenHandle token)=>BitConverter.ToUInt64(Query(token,10,56,BrokerSecurityCheck.TokenChanged));
    private static void CheckTokenUnchanged(SafeProcessHandle process,ulong expected)
    {
        if(!OpenProcessToken(process,8,out var fresh))throw Api(BrokerSecurityCheck.TokenChanged);
        using(fresh)if(TokenId(fresh)!=expected)throw Rejected(BrokerSecurityCheck.TokenChanged);
    }
    private static BrokerTokenLink InspectLinked(SafeAccessTokenHandle peer,SafeAccessTokenHandle current)
    {
        var peerSnapshot=Snapshot(peer,BrokerSecurityCheck.PeerElevationQuery);
        var currentSnapshot=Snapshot(current,BrokerSecurityCheck.CurrentElevationQuery);
        // Reject Default/Full-Full/Limited-Limited before querying links. Both
        // actual process tokens must participate in an opposite UAC split pair.
        if((peerSnapshot.ElevationType,currentSnapshot.ElevationType) is not((2,3) or (3,2)))
            throw Rejected(BrokerSecurityCheck.LinkedDirection);
        var currentLinked=ReadLinkedSnapshot(current,BrokerSecurityCheck.CurrentLinkedTokenQuery);
        var peerLinked=ReadLinkedSnapshot(peer,BrokerSecurityCheck.PeerLinkedTokenQuery);
        if(Snapshot(peer,BrokerSecurityCheck.PeerElevationQuery)!=peerSnapshot ||
            Snapshot(current,BrokerSecurityCheck.CurrentElevationQuery)!=currentSnapshot)
            throw Rejected(BrokerSecurityCheck.TokenChanged);
        return new(peerSnapshot,currentSnapshot,currentLinked,peerLinked);
    }
    // Query-only handles; no duplication, impersonation or privilege adjustment.
    // Internal access supports a real Windows API access-denied regression.
    internal static BrokerTokenSnapshot ReadLinkedSnapshot(SafeAccessTokenHandle source,BrokerSecurityCheck check)
    {
        byte[] value=Query(source,19,IntPtr.Size,check);
        using var linked=new SafeAccessTokenHandle(IntPtr.Size==8?new IntPtr(BitConverter.ToInt64(value)):new IntPtr(BitConverter.ToInt32(value)));
        if(linked.IsInvalid)throw Rejected(check,BrokerSecurityOutcome.InvalidData);
        return Snapshot(linked,check);
    }
    private static byte[] Query(SafeAccessTokenHandle token,int kind,int size,BrokerSecurityCheck check)
    {
        byte[] data=new byte[size];
        if(!GetTokenInformation(token,kind,data,size,out int returned))throw Api(check);
        if(returned!=size)throw Rejected(check,BrokerSecurityOutcome.InvalidData);
        return data;
    }
    private static int IntValue(SafeAccessTokenHandle token,int kind,BrokerSecurityCheck check)=>BitConverter.ToInt32(Query(token,kind,4,check));
    private static ulong AuthenticationId(SafeAccessTokenHandle token,BrokerSecurityCheck check)=>BitConverter.ToUInt64(Query(token,10,56,check),8);
    private static T VariableQuery<T>(SafeAccessTokenHandle token,int kind,BrokerSecurityCheck check,Func<IntPtr,int,T> read)
    {
        bool first=GetTokenBuffer(token,kind,IntPtr.Zero,0,out int size);
        int error=Marshal.GetLastWin32Error();
        if(!first && error!=122)throw new BrokerSecurityException(new(check,BrokerSecurityOutcome.ApiFailure,error));
        if(size<8 || size>65536)throw Rejected(check,BrokerSecurityOutcome.InvalidData);
        IntPtr buffer=Marshal.AllocHGlobal(size);
        try
        {
            if(!GetTokenBuffer(token,kind,buffer,size,out int returned))throw Api(check);
            if(returned<8 || returned>size)throw Rejected(check,BrokerSecurityOutcome.InvalidData);
            return read(buffer,returned);
        }
        finally {Marshal.FreeHGlobal(buffer);}
    }
    private static SecurityIdentifier ReadSid(IntPtr buffer,int size,IntPtr sid,BrokerSecurityCheck check)
    {
        long offset=sid.ToInt64()-buffer.ToInt64();
        if(offset<0 || offset>size-8)throw Rejected(check,BrokerSecurityOutcome.InvalidData);
        int length=8+4*Marshal.ReadByte(sid,1);
        if(Marshal.ReadByte(sid)!=1 || length>68 || length>size-offset)throw Rejected(check,BrokerSecurityOutcome.InvalidData);
        return new SecurityIdentifier(sid);
    }
    private static SecurityIdentifier User(SafeAccessTokenHandle token,BrokerSecurityCheck check)=>
        VariableQuery(token,1,check,(buffer,size)=>ReadSid(buffer,size,Marshal.ReadIntPtr(buffer),check));
    private static bool IsAdministratorGroupEnabled(SafeAccessTokenHandle token)=>VariableQuery(token,2,BrokerSecurityCheck.GroupsQuery,(buffer,size)=>
    {
        int count=Marshal.ReadInt32(buffer),stride=IntPtr.Size==8?16:8,offset=IntPtr.Size;
        if(count<0 || count>(size-offset)/stride)throw Rejected(BrokerSecurityCheck.GroupsQuery,BrokerSecurityOutcome.InvalidData);
        for(int i=0;i<count;i++)
        {
            var sid=ReadSid(buffer,size,Marshal.ReadIntPtr(buffer,offset+i*stride),BrokerSecurityCheck.GroupsQuery);
            int flags=Marshal.ReadInt32(buffer,offset+i*stride+IntPtr.Size);
            if((flags&4)!=0 && (flags&16)==0 && sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))return true;
        }
        return false;
    });
    [DllImport("advapi32.dll",EntryPoint="GetTokenInformation",SetLastError=true)]private static extern bool GetTokenBuffer(SafeAccessTokenHandle token,int kind,IntPtr buffer,int size,out int length);
    public static SafeProcessHandle ObserveProcess(Process process)
    {
        var handle=OpenProcess(0x101000,false,process.Id); // synchronize + query limited; no write/control access
        if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new BrokerSecurityException(new(BrokerSecurityCheck.ProcessOpen,BrokerSecurityOutcome.ApiFailure,error));}
        return handle;
    }
    public static long CreationTime(Process process)
    {
        using var handle=ObserveProcess(process);
        if(HasExited(handle))throw Rejected(BrokerSecurityCheck.ProcessAlive,BrokerSecurityOutcome.ProcessExited);
        if(!GetProcessTimes(handle,out long created,out _,out _,out _))throw Api(BrokerSecurityCheck.CreationTimeQuery);
        return created;
    }
    public static int? ExitCode(SafeProcessHandle handle)=>HasExited(handle) && GetExitCodeProcess(handle,out int code)?code:null;
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetExitCodeProcess(SafeProcessHandle process,out int code);
    public static bool HasExited(SafeProcessHandle handle)
    {
        uint state=WaitForSingleObject(handle,0);
        return state switch {0=>true,258=>false,_=>throw new IOException("Owned broker process observation failed.")};
    }
    public static Task<bool> WaitForExitAsync(SafeProcessHandle handle,TimeSpan timeout)=>BrokerStartup.WaitForCleanupAsync(()=>HasExited(handle),timeout);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint timeout);
    public static BrokerAuthorizationMode ValidateWorker(Process worker,Process bootstrap)
    {
        ValidateWorkerOrigin(worker,bootstrap);
        return ValidatePeer(worker,Path.Combine(AppContext.BaseDirectory,"broker","Northpass.Broker.Worker.exe"),CreationTime(worker),true);
    }
    public static void ValidateWorkerOrigin(Process worker,Process bootstrap)
    {
        if(CreationTime(worker)<CreationTime(bootstrap))throw Rejected(BrokerSecurityCheck.WorkerCreationOrder);
        using var snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot.IsInvalid)throw Api(BrokerSecurityCheck.SnapshotOpen);
        var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>()};bool matched=false;
        bool more=Process32First(snapshot,ref entry);
        while(more)
        {
            if(entry.Pid==worker.Id){matched=entry.ParentPid==bootstrap.Id;break;}
            more=Process32Next(snapshot,ref entry);
        }
        if(!more && Marshal.GetLastWin32Error()!=18)throw Api(BrokerSecurityCheck.SnapshotRead);
        if(!matched)throw Rejected(BrokerSecurityCheck.WorkerParent);
        // Advisory evidence needs only held bootstrap ownership and exact image;
        // it must still be available when subsequent token authorization fails.
        using var queried=ObserveProcess(worker);
        ValidateImage(queried,Path.Combine(AppContext.BaseDirectory,"broker","Northpass.Broker.Worker.exe"));
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
