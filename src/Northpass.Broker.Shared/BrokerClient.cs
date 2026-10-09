using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace Northpass.Broker;

public sealed class ElevationDeclinedException : IOException
{
    public ElevationDeclinedException(Exception inner) : base("Permission to initialize the network module was declined. You can try again.",inner) { }
}
public sealed class BrokerClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private NamedPipeServerStream? _pipe;
    private Process? _helper;
    private SafeProcessHandle? _helperHandle;
    private bool OwnedAlive=>OperatingSystem.IsWindows() && _helper is not null && _helperHandle is not null && !BrokerSecurity.HasExited(_helperHandle);
    private List<FileStream>? _leases;
    private uint _sequence;
    private BrokerStartupEvidence? _evidence;
    private readonly Stopwatch _clock=new();
    private BrokerStage _stage;
    private int? _lastHelperExit;
    private BrokerEvidenceRecord[] _failureEvidence=[];
    public BrokerFailure? LastFailure { get; private set; }
    private void Stage(BrokerStage stage) { _stage=stage; LogReceived?.Invoke($"BROKER_CLIENT stage={stage} elapsed_ms={_clock.ElapsedMilliseconds}"); }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RecordFailure()
    {
        if(LastFailure is null)return;
        LogReceived?.Invoke(LastFailure.Diagnostic);
        // Do not make elevated writes to a per-user diagnostic directory.
        if(BrokerSecurity.IsAdministrator)return;
        try {BrokerFailureJournal.Save(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Northpass","diagnostics"),LastFailure,_failureEvidence);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){LogReceived?.Invoke($"BROKER_JOURNAL unavailable code={BrokerStartup.SafeCode(ex)}; original failure retained");}
    }
    public int AuthenticatedWorkerIdForAcceptance { get; private set; }
    private readonly string? _testHandoff;
    public BrokerClient(string? testHandoff=null) => _testHandoff=testHandoff;
    public event Action<string>? LogReceived;
    public static Exception ElevationError(Win32Exception ex) => ex.NativeErrorCode==1223 ? new ElevationDeclinedException(ex) : ex;
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task ConnectCoreAsync(CancellationToken token)
    {
        if(_pipe is {IsConnected:true} && OwnedAlive)return;
        await CleanupAsync();
        if(_helper is not null){LastFailure=new(BrokerFailureKind.CleanupIncomplete,BrokerStage.Cleanup,0,0,null,false);throw new BrokerStartupException(LastFailure);}
        _clock.Restart();LastFailure=null;_lastHelperExit=null;_failureEvidence=[];
        try
        {
            token.ThrowIfCancellationRequested();
            Stage(BrokerStage.Installation);_leases=BrokerSecurity.VerifyHelper();
            string id=BrokerPipe.NewId();_pipe=BrokerPipe.CreateServer(id);
            _evidence=new BrokerStartupEvidence(id,line=>LogReceived?.Invoke(line));
            using var current=Process.GetCurrentProcess();long creation=current.StartTime.ToUniversalTime().ToFileTimeUtc();
            string arguments=$"--owner {Environment.ProcessId} --created {creation} --pipe {id}";
            Stage(BrokerStage.Elevation);
            token.ThrowIfCancellationRequested();
            if(_testHandoff is null)
                _helper=Process.Start(new ProcessStartInfo(BrokerSecurity.HelperPath,arguments){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(BrokerSecurity.HelperPath)!}) ?? throw new IOException("Trusted helper did not start.");
            else
            {
                // Internal CI handoff: real medium UI + pre-elevated helper. This is
                // explicitly not interactive UAC approval automation or a trust bypass.
                File.Delete(_testHandoff+".pid");
                await File.WriteAllTextAsync(_testHandoff+".pending",JsonSerializer.Serialize(new { Owner=Environment.ProcessId,Created=creation,Pipe=id }),token);
                File.Move(_testHandoff+".pending",_testHandoff,true);
                for(int attempt=0;!File.Exists(_testHandoff+".pid");attempt++) { if(attempt>500)throw new TimeoutException("CI helper handoff expired.");await Task.Delay(20,token); }
                _helper=Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(_testHandoff+".pid",token)));
            }
            _helperHandle=BrokerSecurity.ObserveProcess(_helper);_evidence.Own(_helper);
            LogReceived?.Invoke($"BROKER_CLIENT elevation=helper-returned pid={_helper.Id}; interactive approval inferred only on shell runas path");
            using var observation=new CancellationTokenSource();
            var exited=ObserveExitAsync(_helperHandle,observation.Token);
            using var startup=CancellationTokenSource.CreateLinkedTokenSource(token);startup.CancelAfter(TimeSpan.FromSeconds(15));var startupElapsed=Stopwatch.StartNew();
            try {
            for(int attempt=0;attempt<16;attempt++)
            {
                Stage(BrokerStage.PipeConnect);
                await BrokerStartup.AwaitAsync(_pipe.WaitForConnectionAsync(startup.Token),exited,token,TimeSpan.FromSeconds(15)-startupElapsed.Elapsed);
                using var worker=Process.GetProcessById(BrokerPipe.PeerPid(_pipe,true));
                Stage(BrokerStage.PeerIdentity);
                bool ownedOrigin=false;
                try { BrokerSecurity.ValidateWorkerOrigin(worker,_helper);ownedOrigin=true;BrokerSecurity.ValidateWorker(worker,_helper); }
                catch(UnauthorizedAccessException ex){LogReceived?.Invoke($"BROKER_PEER rejected code={BrokerStartup.SafeCode(ex)} detail={(ex as BrokerSecurityException)?.Detail?.Diagnostic??"unavailable"}");_pipe.Disconnect();if(ownedOrigin)throw;continue;}
                AuthenticatedWorkerIdForAcceptance=worker.Id;
                Stage(BrokerStage.Authentication);
                string secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await BrokerStartup.AwaitAsync(BrokerProtocol.WriteAsync(_pipe,"HELLO 3 "+secret,startup.Token),exited,token,TimeSpan.FromSeconds(15)-startupElapsed.Elapsed);
                var reading=BrokerProtocol.ReadAsync<string>(_pipe,startup.Token);
                await BrokerStartup.AwaitAsync(reading,exited,token,TimeSpan.FromSeconds(15)-startupElapsed.Elapsed);
                string response=await reading;
                if(response!="AUTH 3 "+secret)throw new UnauthorizedAccessException("Broker authentication failed.");
                _sequence=0;return;
            }
            throw new UnauthorizedAccessException("Broker owner authentication attempt limit reached.");
            } finally {observation.Cancel();try{await exited;}catch(OperationCanceledException){}}
        }
        catch(Exception ex)
        {
            int? exit=_helperHandle is null?null:BrokerSecurity.ExitCode(_helperHandle);
            if(exit is not null && _evidence is {} completed)await completed.DrainExitedAsync();
            var detail=_evidence?.Last;
            BrokerFailureKind kind=BrokerStartup.Classify(ex,token.IsCancellationRequested,_stage,exit);
            var stage=detail?.Stage??_stage;
            // Worker terminal codes also survive a broken evidence channel.
            stage=BrokerStartup.ExitStage(exit)??stage;
            long elapsed=_clock.ElapsedMilliseconds;
            bool cleaned=await CleanupAsync();
            LastFailure=new(kind,stage,elapsed,detail is {Code:not 0}?detail.Code:BrokerStartup.SafeCode(ex),exit??_lastHelperExit,cleaned,(ex as BrokerSecurityException)?.Detail??detail?.Security);
            RecordFailure();
            throw new BrokerStartupException(LastFailure,ex);
        }
    }
    public async Task<BrokerResponse> RequestAsync(string command,string engine="zapret1",string strategy="",int? port=null,string transport="both",
        string tcp="12",string udp="12",Dictionary<string,string>? lists=null,CancellationToken token=default)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        await _gate.WaitAsync(token);
        try
        {
            if(command is "STATUS" or "STOP" or "METRICS" && (!OwnedAlive || _pipe is not {IsConnected:true}))
                throw new IOException("The owned privileged helper is unavailable; reconnect explicitly.");
            await ConnectCoreAsync(token);if(_sequence==uint.MaxValue)throw new InvalidDataException("Broker sequence exhausted.");
            var request=new BrokerRequest(3,++_sequence,command,engine,strategy,port,transport,tcp,udp,lists);BrokerProtocol.Validate(request,_sequence);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(60));
            if(command is not("STATUS" or "METRICS"))Stage(command=="START"?BrokerStage.EngineStart:BrokerStage.Preparing);
            await BrokerProtocol.WriteAsync(_pipe!,request,deadline.Token);
            var response=await BrokerProtocol.ReadAsync<BrokerResponse>(_pipe!,deadline.Token);
            if(response.Version!=3 || response.Sequence!=_sequence || response.Error.Length>2048 || response.Logs is {Length:>16})throw new InvalidDataException("Broker response schema rejected.");
            foreach(var line in response.Logs??[])LogReceived?.Invoke(line.Length<=2048?line:line[..2048]);
            if(!response.Success)
            {
                bool cleaned=await CleanupAsync();
                LastFailure=new(command=="START"?BrokerFailureKind.EngineLaunchFailed:BrokerFailureKind.LaunchFailed,_stage,_clock.ElapsedMilliseconds,response.SafeCode,_lastHelperExit,cleaned);
                RecordFailure();
                // Detail is returned only across the fully authenticated control pipe.
                LogReceived?.Invoke("BROKER_ENGINE "+response.Error);
                throw new BrokerStartupException(LastFailure);
            }
            if(command is not("STATUS" or "METRICS"))Stage(BrokerStage.Ready);
            return response;
        }
        catch(BrokerStartupException){throw;}
        catch(Exception ex) {int? exit=_helperHandle is null?null:BrokerSecurity.ExitCode(_helperHandle);bool cleaned=await CleanupAsync();LastFailure=new(token.IsCancellationRequested?BrokerFailureKind.Cancelled:BrokerFailureKind.IpcDisconnected,_stage,_clock.ElapsedMilliseconds,BrokerStartup.SafeCode(ex),exit??_lastHelperExit,cleaned);RecordFailure();throw new BrokerStartupException(LastFailure,ex);}
        finally {_gate.Release();}
    }
    // Internal headless acceptance only; never called by normal UI commands.
    public async Task VerifyReplayRejectionForAcceptanceAsync()
    {
        await _gate.WaitAsync();
        try {
            if(_pipe is not {IsConnected:true} || _sequence==0)throw new InvalidOperationException("Authenticated session required.");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await BrokerProtocol.WriteAsync(_pipe,new BrokerRequest(3,_sequence,"STATUS"),timeout.Token);
            try { _=await BrokerProtocol.ReadAsync<BrokerResponse>(_pipe,timeout.Token);throw new InvalidDataException("Privileged broker accepted replayed sequence."); }
            catch(EndOfStreamException){ }
            catch(IOException){ }
            await CleanupAsync();
        } finally {_gate.Release();}
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task ObserveExitAsync(SafeProcessHandle handle,CancellationToken token)
    {while(!BrokerSecurity.HasExited(handle))await Task.Delay(25,token).ConfigureAwait(false);}
    private async Task<bool> CleanupAsync()
    {
        _pipe?.Dispose();_pipe=null;
        if(OperatingSystem.IsWindows() && _evidence is {} evidence){await evidence.DisposeAsync();_failureEvidence=evidence.Records;_evidence=null;}
        AuthenticatedWorkerIdForAcceptance=0;
        if(OperatingSystem.IsWindows() && _helper is {} helper)
        {
            try {
            // Medium UI cannot force-kill elevated workers. Pipe/owner closure is
            // the shutdown authority; retain/report the PID if shutdown times out.
            if(_helperHandle is null)_helperHandle=BrokerSecurity.ObserveProcess(helper);
            if(!await BrokerSecurity.WaitForExitAsync(_helperHandle,TimeSpan.FromSeconds(8)))
                LogReceived?.Invoke("Privileged helper cleanup timed out; PID "+helper.Id+" remains owned until it exits.");
            if(BrokerSecurity.HasExited(_helperHandle)){_lastHelperExit=BrokerSecurity.ExitCode(_helperHandle);_helperHandle.Dispose();_helperHandle=null;helper.Dispose();_helper=null;}
            } catch(Exception ex)when(ex is Win32Exception or IOException or UnauthorizedAccessException){LogReceived?.Invoke($"BROKER_CLEANUP observation_failed code={BrokerStartup.SafeCode(ex)} pid={helper.Id}; ownership and leases retained");}
        }
        if(_helper is null && _leases is {} leases){foreach(var lease in leases)lease.Dispose();_leases=null;}
        LogReceived?.Invoke("BROKER_CLEANUP completed="+(_helper is null));
        return _helper is null;
    }
    public async ValueTask DisposeAsync(){await _gate.WaitAsync();try{await CleanupAsync();}finally{_gate.Release();}}
}
