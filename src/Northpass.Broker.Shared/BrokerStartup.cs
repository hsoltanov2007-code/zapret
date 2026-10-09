using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace Northpass.Broker;

public enum BrokerStage { Installation=1, Elevation=2, BootstrapOwner=3, BootstrapIntegrity=4, WorkerCreate=5, WorkerOwner=6, PipeConnect=7, PeerIdentity=8, Authentication=9, Preparing=10, EngineStart=11, Ready=12, Cleanup=13 }
public enum BrokerFailureKind { UacDenied, Cancelled, StartupTimeout, HelperExited, AuthenticationRejected, InstallationRejected, EngineLaunchFailed, IpcDisconnected, CleanupIncomplete, LaunchFailed }
public sealed record BrokerFailure(BrokerFailureKind Kind, BrokerStage Stage, long ElapsedMilliseconds, int SafeCode, int? HelperExitCode, bool CleanupCompleted, BrokerSecurityDetail? Security=null)
{
    public string Diagnostic => $"BROKER_FAILURE kind={Kind} stage={Stage} elapsed_ms={ElapsedMilliseconds} code={SafeCode} helper_exit={HelperExitCode?.ToString(CultureInfo.InvariantCulture)??"unavailable"} cleanup={(CleanupCompleted?"complete":"pending")}{(Security is null?"":" "+Security.Diagnostic)}";
    public string MessageKey => Kind switch { BrokerFailureKind.UacDenied=>"PermissionDeclined", BrokerFailureKind.Cancelled=>"ConnectionCancelled", BrokerFailureKind.StartupTimeout=>"BrokerTimeout", BrokerFailureKind.HelperExited=>"BrokerExited", BrokerFailureKind.AuthenticationRejected=>"BrokerRejected", BrokerFailureKind.InstallationRejected=>"ReinstallRequired", BrokerFailureKind.CleanupIncomplete=>"BrokerCleanup", BrokerFailureKind.EngineLaunchFailed=>"ConnectionFailed", _=>"BrokerFailed" };
}
public sealed class BrokerStartupException(BrokerFailure failure, Exception? inner=null) : IOException(failure.Diagnostic,inner)
{ public BrokerFailure Failure { get; }=failure; }
public static class BrokerFailureMessage
{
    public static string? Key(Exception exception)
    {
        if(exception is BrokerStartupException broker)return broker.Failure.MessageKey;
        // EngineStatus retains the bounded diagnostic string, not an exception.
        foreach(var kind in Enum.GetValues<BrokerFailureKind>())
            if(exception.Message.StartsWith($"BROKER_FAILURE kind={kind} stage=",StringComparison.Ordinal))return new BrokerFailure(kind,BrokerStage.Installation,0,0,null,true).MessageKey;
        return null;
    }
}

// Portable coordinator also used by the actual pipe wait: an owned-process exit
// wins over a deadline, including when both become observable together.
public static class BrokerStartup
{
    public static BrokerFailureKind Classify(Exception ex,bool callerCancelled,BrokerStage stage,int? exit) => ex switch
    {
        Win32Exception w when w.NativeErrorCode==1223 && stage==BrokerStage.Elevation=>BrokerFailureKind.UacDenied,
        OperationCanceledException when callerCancelled=>BrokerFailureKind.Cancelled,
        UnauthorizedAccessException when stage!=BrokerStage.Installation=>BrokerFailureKind.AuthenticationRejected,
        UnauthorizedAccessException or InvalidDataException=>BrokerFailureKind.InstallationRejected,
        BrokerHelperExitedException=>ExitKind(exit)??BrokerFailureKind.HelperExited,
        _ when exit is not null=>ExitKind(exit)??BrokerFailureKind.HelperExited,
        OperationCanceledException or TimeoutException=>BrokerFailureKind.StartupTimeout,
        _=>BrokerFailureKind.LaunchFailed
    };
    public static BrokerStage? ExitStage(int? exit)
    {
        if(exit is not {} value || (value&unchecked((int)0xffff0000)) is not(0x4e500000 or 0x4e510000))return null;
        var stage=(BrokerStage)((value>>8)&255);return Enum.IsDefined(stage)?stage:null;
    }
    public static BrokerFailureKind? ExitKind(int? exit)
    {
        var stage=ExitStage(exit);
        if(stage is null || exit is not {} code)return null;
        if((code&255)==2)return stage==BrokerStage.BootstrapIntegrity?BrokerFailureKind.InstallationRejected:BrokerFailureKind.AuthenticationRejected;
        if((code&255)==3)return stage is BrokerStage.Authentication or BrokerStage.PeerIdentity?BrokerFailureKind.AuthenticationRejected:BrokerFailureKind.InstallationRejected;
        if((code&255)==4)return BrokerFailureKind.StartupTimeout;
        return null; // crash/arbitrary exit never becomes an inferred rejection
    }
    public static async Task AwaitAsync(Task operation,Task ownedExit,CancellationToken caller,TimeSpan timeout)
    {
        using var deadline=new CancellationTokenSource();
        if(timeout<=TimeSpan.Zero)deadline.Cancel();else deadline.CancelAfter(timeout);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(caller,deadline.Token);
        var cancelled=Task.Delay(Timeout.InfiniteTimeSpan,linked.Token);
        await Task.WhenAny(operation,ownedExit,cancelled).ConfigureAwait(false);
        if(!operation.IsCompleted)_=operation.ContinueWith(task=>{_ = task.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        caller.ThrowIfCancellationRequested();
        if(ownedExit.IsCompleted) { await ownedExit.ConfigureAwait(false); throw new BrokerHelperExitedException(); }
        if(operation.IsCompleted) { await operation.ConfigureAwait(false); return; }
        throw new TimeoutException("Owned broker IPC startup deadline expired.");
    }
    public static int SafeCode(Exception exception) => exception is BrokerSecurityException security ? security.Detail.Code : exception is Win32Exception windows ? windows.NativeErrorCode : exception.HResult;
    public static async Task<bool> WaitForCleanupAsync(Func<bool> hasExited,TimeSpan timeout)
    {
        var clock=Stopwatch.StartNew();
        while(!hasExited()){if(clock.Elapsed>=timeout)return false;await Task.Delay(25).ConfigureAwait(false);}
        return true;
    }
}
public sealed class BrokerHelperExitedException : IOException { public BrokerHelperExitedException():base("Owned elevated helper exited before the operation completed.") { } }

// Advisory stage data never authorizes commands. Fixed numeric schema, no
// account names, paths, command lines, nonces, packet data or exception text.
public sealed record BrokerEvidenceRecord(BrokerStage Stage,long ElapsedMilliseconds,int Code,int WorkerId,BrokerSecurityDetail? Security=null)
{
    public static BrokerEvidenceRecord Parse(string line)
    {
        if(line.Length>128)throw new InvalidDataException("Broker evidence exceeds bounds.");
        string[] fields=line.Split(' ');
        bool detailed=fields.Length==9 && fields[0]=="NPB2";
        if((!detailed && (fields.Length!=5 || fields[0]!="NPB1")) || !int.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out int stage) || !Enum.IsDefined((BrokerStage)stage) ||
            !long.TryParse(fields[2],NumberStyles.None,CultureInfo.InvariantCulture,out long elapsed) || elapsed>3600000 ||
            !int.TryParse(fields[3],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int code) ||
            !int.TryParse(fields[4],NumberStyles.None,CultureInfo.InvariantCulture,out int worker))throw new InvalidDataException("Broker evidence schema rejected.");
        BrokerSecurityDetail? detail=null;
        if(detailed)
        {
            if(!int.TryParse(fields[5],NumberStyles.None,CultureInfo.InvariantCulture,out int check) || !Enum.IsDefined((BrokerSecurityCheck)check) || check==0 ||
               !int.TryParse(fields[6],NumberStyles.None,CultureInfo.InvariantCulture,out int outcome) || !Enum.IsDefined((BrokerSecurityOutcome)outcome) || outcome==0 ||
               !int.TryParse(fields[7],NumberStyles.None,CultureInfo.InvariantCulture,out int linked) || !Enum.IsDefined((BrokerLinkedTokenStatus)linked) ||
               !int.TryParse(fields[8],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int linkedCode))throw new InvalidDataException("Broker security evidence rejected.");
            detail=new((BrokerSecurityCheck)check,(BrokerSecurityOutcome)outcome,code,(BrokerLinkedTokenStatus)linked,linkedCode);
        }
        return new((BrokerStage)stage,elapsed,code,worker,detail);
    }
    public string Diagnostic=>$"BROKER_STAGE stage={Stage} elapsed_ms={ElapsedMilliseconds} code={Code} worker_pid={WorkerId}{(Security is null?"":" "+Security.Diagnostic)}";
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BrokerStartupEvidence : IAsyncDisposable
{
    private readonly NamedPipeServerStream _bootstrap,_worker;
    private readonly CancellationTokenSource _stop=new();
    private readonly TaskCompletionSource<Process> _owner=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task[] _readers;
    private readonly Action<string> _log;
    private readonly object _sync=new();
    private BrokerEvidenceRecord? _last;
    private readonly Queue<BrokerEvidenceRecord> _records=new();
    public BrokerEvidenceRecord? Last { get {lock(_sync)return _last;} }
    public BrokerEvidenceRecord[] Records { get {lock(_sync)return _records.ToArray();} }
    public async Task DrainExitedAsync()
    {
        // Once the owned helper exited, allow its already-written terminal
        // evidence to reach the reader before cancellation closes the pipes.
        try {await Task.WhenAll(_readers).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);}
        catch(TimeoutException){ }
    }
    public BrokerStartupEvidence(string id,Action<string> log)
    {
        _log=log;_bootstrap=BrokerPipe.CreateEvidenceServer(id,false);
        try {_worker=BrokerPipe.CreateEvidenceServer(id,true);}catch{_bootstrap.Dispose();throw;}
        _readers=[ReadAsync(_bootstrap,false),ReadAsync(_worker,true)];
    }
    public void Own(Process process)=>_owner.TrySetResult(process);
    private async Task ReadAsync(NamedPipeServerStream pipe,bool worker)
    {
        try
        {
            // Limited attempts: unrelated peers may not substitute diagnostic data.
            for(int attempt=0;attempt<16;attempt++)
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var owned=await _owner.Task.WaitAsync(_stop.Token).ConfigureAwait(false);int pid=BrokerPipe.PeerPid(pipe,true);
                try
                {
                    if(worker){using var peer=Process.GetProcessById(pid);BrokerSecurity.ValidateWorkerOrigin(peer,owned);}
                    else if(pid!=owned.Id)throw new UnauthorizedAccessException();
                    break;
                }
                catch(Exception ex)when(ex is UnauthorizedAccessException or ArgumentException or Win32Exception){pipe.Disconnect();if(attempt==15)return;}
            }
            var bytes=new byte[1];var line=new StringBuilder(128);int total=0;
            for(int records=0;records<32;)
            {
                int read=await pipe.ReadAsync(bytes,_stop.Token).ConfigureAwait(false);if(read==0)return;
                if(++total>4096)return;
                if(bytes[0]==10)
                {
                    var record=BrokerEvidenceRecord.Parse(line.ToString());line.Clear();records++;
                    lock(_sync){_last=record;_records.Enqueue(record);while(_records.Count>64)_records.Dequeue();}
                    _log(record.Diagnostic);
                }
                else {if(bytes[0]<32 || bytes[0]>126 || line.Length>=128)return;line.Append((char)bytes[0]);}
            }
            // Leave the bootstrap liveness handle open. Closing it is the
            // medium desktop's shutdown authority, not a privileged kill.
        }
        catch(Exception ex)when(ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException or UnauthorizedAccessException or ArgumentException or Win32Exception){ }
    }
    public async ValueTask DisposeAsync(){_stop.Cancel();_bootstrap.Dispose();_worker.Dispose();await Task.WhenAll(_readers).ConfigureAwait(false);_stop.Dispose();}
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BrokerEvidenceWriter : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private int _records;
    public static async Task<BrokerEvidenceWriter> ConnectAsync(string id,int owner)
    {
        var writer=new BrokerEvidenceWriter();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try {writer._pipe=await BrokerPipe.ConnectEvidenceAsync(id,true,timeout.Token);if(BrokerPipe.PeerPid(writer._pipe,false)!=owner)throw new UnauthorizedAccessException();}
        catch{writer.Dispose();} // advisory only; authentication still mandatory
        return writer;
    }
    public void Report(BrokerStage stage,int code=0,BrokerSecurityDetail? security=null)
    {
        if(_pipe is null || _records++>=32)return;
        string message=security is null
            ? FormattableString.Invariant($"NPB1 {(int)stage} {_clock.ElapsedMilliseconds} {code} {Environment.ProcessId}\n")
            : FormattableString.Invariant($"NPB2 {(int)stage} {_clock.ElapsedMilliseconds} {code} {Environment.ProcessId} {(int)security.Check} {(int)security.Outcome} {(int)security.Linked} {security.LinkedCode}\n");
        byte[] line=Encoding.ASCII.GetBytes(message);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try {_pipe.WriteAsync(line,timeout.Token).AsTask().GetAwaiter().GetResult();}catch{Dispose();}
    }
    public void Dispose(){_pipe?.Dispose();_pipe=null;}
}
