using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Northpass.Broker;
using Northpass.Engine;
using Northpass.Engine.Native;
using Northpass.Engine.Zapret1;
using Northpass.Models;
namespace Northpass.Desktop;

internal static class BrokerAcceptance
{
    public static async Task CheckAsync(BrokerClient client,IEngineInstallationManager flowseal,IEngineInstallationManager native,string[] args)
    {
        if(BrokerSecurity.IsAdministrator)throw new IOException("Broker acceptance must run as the actual medium desktop user.");
        int modeIndex=Array.IndexOf(args,"--broker-mode");string mode=modeIndex>=0?args[modeIndex+1]:"native";
        var logs=new List<string>();client.LogReceived+=line=>{logs.Add(line);if(logs.Count>32)logs.RemoveAt(0);};
        if(mode=="early-worker-exit")
        {
            try {await client.RequestAsync("DETECT","native");throw new IOException("Substituted owner image unexpectedly authenticated.");}
            catch(BrokerStartupException ex)
            {
                if(ex.Failure.Kind!=BrokerFailureKind.HelperExited || ex.Failure.Stage!=BrokerStage.WorkerOwner || !ex.Failure.CleanupCompleted || ex.Failure.HelperExitCode is null)throw;
                int evidence=Array.IndexOf(args,"--evidence");
                await File.WriteAllTextAsync(args[evidence+1],JsonSerializer.Serialize(new { Failure=ex.Failure,Logs=logs,NoEngineStarted=true }));
                return;
            }
        }
        if(mode=="startup-retry")
        {
            using var cancel=new CancellationTokenSource();
            void CancelAtWait(string line){if(line.StartsWith("BROKER_CLIENT stage=PipeConnect",StringComparison.Ordinal))cancel.Cancel();}
            client.LogReceived+=CancelAtWait;
            try {await client.RequestAsync("DETECT","native",token:cancel.Token);throw new IOException("Startup cancellation unexpectedly succeeded.");}
            catch(BrokerStartupException ex){if(ex.Failure.Kind!=BrokerFailureKind.Cancelled || !ex.Failure.CleanupCompleted || ex.Failure.HelperExitCode is null)throw;}
            finally {client.LogReceived-=CancelAtWait;}
            // Same actual client retries with a fresh nonce/helper/leases.
            await client.RequestAsync("DETECT","native");
        }
        if(mode=="repair")await native.RepairAsync();
        var installed=await native.EnsureInstalledAsync();
        using var receiver=new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback,0));
        using var sender=new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback,0));
        int port=((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        if(port is <49152 or >65535)throw new IOException("A dedicated reserved loopback socket is required.");
        await using var engine=new BrokerEngine(client,"native");
        bool faultNotification=false;engine.StatusChanged+=status=>{if(status.State==EngineState.Error)faultNotification=true;};
        await engine.StartAsync(new(installed.ExecutablePath,NativeCatalog.Loopback(port,"udp")));
        if((await engine.GetStatusAsync()).State!=EngineState.Active)throw new IOException("Real broker/native initialization failed.");
        byte[] original=Enumerable.Range(0,128).Select(i=>(byte)i).ToArray();
        for(int i=0;i<64;i++)
        {
            var received=receiver.ReceiveAsync();await sender.SendAsync(original,new IPEndPoint(IPAddress.IPv6Loopback,port));
            if(!(await received.WaitAsync(TimeSpan.FromSeconds(5))).Buffer.SequenceEqual(original))throw new IOException("Loopback packet integrity failed.");
        }
        await Task.Delay(100);var metrics=await engine.GetPerformanceAsync();
        if(metrics is not {KernelLossUnknown:true,KnownDropped:0} || metrics.Captured<64)throw new IOException("Native reliability metrics failed.");
        int nativePid=(await engine.GetStatusAsync()).ProcessId!.Value;
        if(mode=="disconnect-active")
        {
            await client.DisposeAsync();
            try{await engine.StopAsync();throw new IOException("Disconnected helper unexpectedly accepted STOP.");}
            catch(IOException){if((await engine.GetStatusAsync()).State!=EngineState.Error)throw;}
        }
        else if(mode=="worker-crash")
        {
            int handoff=Array.IndexOf(args,"--broker-test-handoff");
            await File.WriteAllTextAsync(args[handoff+1]+".crash",JsonSerializer.Serialize(new{Worker=client.AuthenticatedWorkerIdForAcceptance,Native=nativePid}));
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while((await engine.GetStatusAsync()).State!=EngineState.Error){if(DateTime.UtcNow>=deadline)throw new IOException("Worker crash was not detected.");await Task.Delay(50);}
            if(!faultNotification)throw new IOException("Polled worker failure did not notify controller recovery before STOP.");
            try{await engine.StopAsync();}catch(IOException){ } // failure is reported, never silently re-elevated
        }
        else if(mode!="parent-death")await engine.StopAsync();
        if(mode=="flowseal")
        {
            if(!(await client.RequestAsync("STATUS")).NoTrafficTest)throw new IOException("Flowseal acceptance requires verified no-traffic test composition.");
            var fallback=await flowseal.EnsureInstalledAsync();
            string testLists=Path.Combine(Path.GetTempPath(),"northpass-broker-lists-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(testLists);
            try {
                string input=Path.Combine(testLists,"source.txt");await File.WriteAllTextAsync(input,"example.com\n");
                var store=new Northpass.Services.DataListStore(testLists);
                var profile=FlowsealCatalog.Profile(FlowsealCatalog.Strategies[0]);profile.ListBindings["general"]=store.Import(input,Northpass.Services.DataListKind.Hosts).Id;
                await using var checkedEngine=new BrokerEngine(client,"zapret1",store);
                for(int repeat=0;repeat<2;repeat++) {
                    await checkedEngine.StartAsync(new(fallback.ExecutablePath,profile));
                    if((await checkedEngine.GetStatusAsync()).State!=EngineState.Active)throw new IOException("Broker Flowseal readiness failed.");
                    await checkedEngine.StopAsync(); // verified filter=false; also exercises bounded private list copy/reuse
                }
            } finally {Directory.Delete(testLists,true);}
        }
        if(mode=="replay")await client.VerifyReplayRejectionForAcceptanceAsync();
        if(mode is "worker-crash" or "disconnect-active" && !faultNotification)throw new IOException("Owned failure did not notify the replaceable controller.");
        var result=new{UiAdministrator=false,AuthenticatedElevatedWorker=true,Mode=mode,NativePid=nativePid,Ipv6UnchangedDatagrams=64,Metrics=metrics,
            Scope="Dedicated loopback only; Flowseal check requires explicit filter=false test bootstrap. No DPI bypass/hardware certification.",Logs=logs};
        int output=Array.IndexOf(args,"--evidence");if(output>=0)await File.WriteAllTextAsync(args[output+1],JsonSerializer.Serialize(result));
        if(mode=="parent-death")Environment.Exit(0); // deliberately bypass OnExit/finally with active owned engine
    }
}
