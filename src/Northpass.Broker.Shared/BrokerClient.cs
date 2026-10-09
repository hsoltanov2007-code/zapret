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
    private readonly string? _testHandoff;
    public BrokerClient(string? testHandoff=null) => _testHandoff=testHandoff;
    public event Action<string>? LogReceived;
    public static Exception ElevationError(Win32Exception ex) => ex.NativeErrorCode==1223 ? new ElevationDeclinedException(ex) : ex;
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task ConnectCoreAsync(CancellationToken token)
    {
        if(_pipe is {IsConnected:true} && OwnedAlive)return;
        await CleanupAsync();
        if(_helper is not null)throw new IOException("The owned privileged helper has not exited. Retry cleanup before starting another.");
        try
        {
            _leases=BrokerSecurity.VerifyHelper();
            string id=BrokerPipe.NewId();_pipe=BrokerPipe.CreateServer(id);
            using var current=Process.GetCurrentProcess();long creation=current.StartTime.ToUniversalTime().ToFileTimeUtc();
            string arguments=$"--owner {Environment.ProcessId} --created {creation} --pipe {id}";
            if(_testHandoff is null)
                _helper=Process.Start(new ProcessStartInfo(BrokerSecurity.HelperPath,arguments){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden}) ?? throw new IOException("Trusted helper did not start.");
            else
            {
                // Internal CI handoff: real medium UI + pre-elevated helper. This is
                // explicitly not interactive UAC approval automation or a trust bypass.
                await File.WriteAllTextAsync(_testHandoff,JsonSerializer.Serialize(new { Owner=Environment.ProcessId,Created=creation,Pipe=id }),token);
                for(int attempt=0;!File.Exists(_testHandoff+".pid");attempt++) { if(attempt>500)throw new TimeoutException("CI helper handoff expired.");await Task.Delay(20,token); }
                _helper=Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(_testHandoff+".pid",token)));
            }
            _helperHandle=BrokerSecurity.ObserveProcess(_helper);
            using var startup=CancellationTokenSource.CreateLinkedTokenSource(token);startup.CancelAfter(TimeSpan.FromSeconds(15));
            for(int attempt=0;attempt<16;attempt++)
            {
                await _pipe.WaitForConnectionAsync(startup.Token);
                using var worker=Process.GetProcessById(BrokerPipe.PeerPid(_pipe,true));
                try { BrokerSecurity.ValidateWorker(worker,_helper); }
                catch(UnauthorizedAccessException){_pipe.Disconnect();continue;}
                string secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await BrokerProtocol.WriteAsync(_pipe,"HELLO 3 "+secret,startup.Token);
                string response=await BrokerProtocol.ReadAsync<string>(_pipe,startup.Token);
                if(response!="AUTH 3 "+secret)throw new UnauthorizedAccessException("Broker authentication failed.");
                _sequence=0;return;
            }
            throw new UnauthorizedAccessException("Broker owner authentication attempt limit reached.");
        }
        catch(Win32Exception ex){await CleanupAsync();throw ElevationError(ex);}
        catch{await CleanupAsync();throw;}
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
            await BrokerProtocol.WriteAsync(_pipe!,request,deadline.Token);
            var response=await BrokerProtocol.ReadAsync<BrokerResponse>(_pipe!,deadline.Token);
            if(response.Version!=3 || response.Sequence!=_sequence || response.Error.Length>2048 || response.Logs is {Length:>16})throw new InvalidDataException("Broker response schema rejected.");
            foreach(var line in response.Logs??[])LogReceived?.Invoke(line.Length<=2048?line:line[..2048]);
            if(!response.Success)throw new IOException(response.Error);
            return response;
        }
        catch { await CleanupAsync();throw; }
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
    private async Task CleanupAsync()
    {
        _pipe?.Dispose();_pipe=null;
        if(OperatingSystem.IsWindows() && _helper is {} helper)
        {
            // Medium UI cannot force-kill elevated workers. Pipe/owner closure is
            // the shutdown authority; retain/report the PID if shutdown times out.
            if(_helperHandle is null)_helperHandle=BrokerSecurity.ObserveProcess(helper);
            if(!await BrokerSecurity.WaitForExitAsync(_helperHandle,TimeSpan.FromSeconds(8)))
                LogReceived?.Invoke("Privileged helper cleanup timed out; PID "+helper.Id+" remains owned until it exits.");
            if(BrokerSecurity.HasExited(_helperHandle)){_helperHandle.Dispose();_helperHandle=null;helper.Dispose();_helper=null;}
        }
        if(_helper is null && _leases is {} leases){foreach(var lease in leases)lease.Dispose();_leases=null;}
    }
    public async ValueTask DisposeAsync(){await _gate.WaitAsync();try{await CleanupAsync();}finally{_gate.Release();}}
}
