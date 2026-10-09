using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Northpass.Broker;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Native;
using Northpass.Engine.Zapret1;
using Northpass.Models;
using Northpass.Services;
using Northpass.Services.Installation;

if (!OperatingSystem.IsWindows()) return 2;
string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
BrokerEvidenceWriter? evidence=null;BrokerStage stage=BrokerStage.WorkerOwner;
void Stage(BrokerStage value){stage=value;evidence?.Report(value);}
try
{
    if (!BrokerSecurity.IsAdministrator)throw new UnauthorizedAccessException("The network helper requires Windows elevation.");
    if (args.SequenceEqual(new[]{"--install"}))
    {
        // Installer-only fixed self location. Never takes an external path/catalog.
        string program=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if(!root.StartsWith(program+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("Install the helper under Program Files.");
        SafeArchive.NoLinks(root); new WindowsInstallationSecurity().ProtectDirectory(root);
        BrokerSecurity.ValidateProtectedApplication(root);
        using var http=new HttpClient();foreach(string id in new[]{"zapret1","native"}) {
            var manager=Manager(id,http,root);
            try {await manager.EnsureInstalledAsync();}
            catch(Exception ex)when(ex is InvalidDataException or FileNotFoundException){await manager.RepairAsync();}
        }
        return 0;
    }
    bool noTraffic=args.Length==7 && args[6]=="--test-no-traffic";
    if((args.Length!=6 && !noTraffic) || args[0]!="--owner" || args[2]!="--created" || args[4]!="--pipe" ||
        !int.TryParse(args[1],NumberStyles.None,CultureInfo.InvariantCulture,out int pid) || pid<=0 ||
        !long.TryParse(args[3],NumberStyles.None,CultureInfo.InvariantCulture,out long created) || !BrokerPipe.ValidId(args[5]))throw new InvalidDataException("Invalid broker launch contract.");
    evidence=await BrokerEvidenceWriter.ConnectAsync(args[5],pid);Stage(BrokerStage.BootstrapIntegrity);
    BrokerSecurity.ValidateProtectedApplication(root);
    Stage(BrokerStage.WorkerOwner);
    using var owner=Process.GetProcessById(pid);
    BrokerSecurity.ValidatePeer(owner,Path.Combine(root,"Northpass.exe"),created,requireAdmin:false);
    using var lifetime=new CancellationTokenSource();
    var monitor=Task.Run(async()=>{try{await owner.WaitForExitAsync(lifetime.Token);lifetime.Cancel();}catch(OperationCanceledException){}});
    Stage(BrokerStage.PipeConnect);
    using var pipe=await BrokerPipe.ConnectAsync(args[5],lifetime.Token);
    Stage(BrokerStage.PeerIdentity);
    if(BrokerPipe.PeerPid(pipe,false)!=pid)throw new UnauthorizedAccessException("Broker pipe server is not the authorized desktop owner.");
    BrokerSecurity.ValidatePeer(owner,Path.Combine(root,"Northpass.exe"),created,requireAdmin:false);
    Stage(BrokerStage.Authentication);
    using(var startup=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
    {
        startup.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            string hello=await BrokerProtocol.ReadAsync<string>(pipe,startup.Token);
            if(hello.Length!=72 || !hello.StartsWith("HELLO 3 ",StringComparison.Ordinal) || !hello[8..].All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F'))throw new InvalidDataException("Invalid broker authentication challenge.");
            await BrokerProtocol.WriteAsync(pipe,"AUTH 3 "+hello[8..],startup.Token);
        } catch(OperationCanceledException ex)when(!lifetime.IsCancellationRequested){throw new TimeoutException("Broker authentication deadline expired.",ex);}
    }
    Stage(BrokerStage.Preparing);
    using var client=new HttpClient();
    var flowseal=Manager("zapret1",client,root);var native=Manager("native",client,root);
    var security=new WindowsInstallationSecurity();
    string dataRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Northpass-BrokerData");security.PrepareRoot(dataRoot);
    string session=Path.Combine(dataRoot,"owner-"+args[5]);Directory.CreateDirectory(session);security.ProtectDirectory(session);
    var store=new DataListStore(session); var data=new ProtectedEngineDataProvider(store,Path.Combine(session,"snapshots"),security);
    var registry=new EngineRegistry();registry.Register(NativeEngine.Metadata,()=>new NativeEngine(native,useNamedPipe:true));
    registry.Register(Zapret1Engine.Metadata,()=>new Zapret1Engine(flowseal,data,noTrafficCapture: noTraffic));
    await using var controller=new EngineController(registry);
    var logs=new ConcurrentQueue<string>();controller.LogReceived+=line=>{logs.Enqueue(line.Length<=2048?line:line[..2048]);while(logs.Count>16)logs.TryDequeue(out _);};
    uint expected=1;
    try
    {
        while(!lifetime.IsCancellationRequested)
        {
            BrokerRequest request;
            try { request=await BrokerProtocol.ReadAsync<BrokerRequest>(pipe,lifetime.Token); }
            catch(EndOfStreamException){break;}
            BrokerProtocol.Validate(request,expected);++expected;
            BrokerResponse response;
            try
            {
                var manager=request.Engine=="native"?native:flowseal;
                InstalledEngine? installed=null;EngineStatus? status=null;EnginePerformance? metrics=null;
                switch(request.Command)
                {
                    case "DETECT":installed=await manager.DetectAsync(lifetime.Token);break;
                    case "INSTALL":installed=await manager.EnsureInstalledAsync(token:lifetime.Token);break;
                    case "REPAIR":installed=await manager.RepairAsync(token:lifetime.Token);break;
                    case "ROLLBACK":installed=await manager.RollbackAsync(lifetime.Token);break;
                    case "START":
                        Stage(BrokerStage.EngineStart);
                        if((await controller.GetStatusAsync(lifetime.Token)).State is EngineState.Active or EngineState.Connecting or EngineState.Stopping)
                            throw new InvalidOperationException("Disconnect the owned session before starting another.");
                        // Discard only previous private input copies; snapshots are
                        // separately leased. Session disk use cannot grow per reconnect.
                        foreach(string copy in Directory.EnumerateFiles(session)) {security.ValidateFile(copy);File.Delete(copy);}
                        installed=await manager.EnsureInstalledAsync(token:lifetime.Token);
                        StrategyProfile profile;
                        if(request.Engine=="native")profile=request.Strategy=="passthrough-idle"?NativeCatalog.Idle():NativeCatalog.Loopback(request.Port!.Value,request.Transport);
                        else
                        {
                            profile=FlowsealCatalog.Profile(FlowsealCatalog.Find(request.Strategy)!);profile.GameTcpPorts=request.TcpPorts;profile.GameUdpPorts=request.UdpPorts;
                            foreach(var(slot,text)in request.Lists??[])
                            {
                                var kind=slot is "general" or "excluded-hosts"?DataListKind.Hosts:DataListKind.IpSet;
                                string source=Path.Combine(session,"incoming-"+slot+".txt");File.WriteAllText(source,DataListStore.Normalize(text,kind));security.ProtectFile(source);
                                profile.ListBindings[slot]=store.Import(source,kind).Id;File.Delete(source);
                            }
                            security.ProtectDirectory(session);
                        }
                        await controller.ConnectAsync(new(installed.ExecutablePath,profile),autoRecover:false,lifetime.Token);status=await controller.GetStatusAsync(lifetime.Token);break;
                    case "STOP":await controller.DisconnectAsync(lifetime.Token);status=await controller.GetStatusAsync(lifetime.Token);break;
                    case "STATUS":status=await controller.GetStatusAsync(lifetime.Token);break;
                    case "METRICS":metrics=await controller.GetPerformanceAsync(lifetime.Token);break;
                    case "SHUTDOWN":await controller.DisconnectAsync(lifetime.Token);break;
                }
                var recent=new List<string>();while(logs.TryDequeue(out var line))recent.Add(line);
                Stage(BrokerStage.Ready);
                response=new(3,request.Sequence,true,Installed:installed,Status:status,Performance:metrics,Logs:recent.ToArray(),NoTrafficTest:noTraffic);
            }
            catch(Exception ex)when(ex is not OperationCanceledException)
            {response=new(3,request.Sequence,false,Error:ex.Message.Length>2048?ex.Message[..2048]:ex.Message,SafeCode:BrokerStartup.SafeCode(ex));}
            await BrokerProtocol.WriteAsync(pipe,response,lifetime.Token);
            if(request.Command=="SHUTDOWN")break;
        }
    }
    finally
    {
        lifetime.Cancel();await monitor;
        await controller.DisconnectAsync();
        await controller.DisposeAsync();
        if(Directory.Exists(session)){SafeArchive.NoLinks(session);Directory.Delete(session,true);}
    }
    return 0;
}
catch(Exception ex){evidence?.Report(stage,BrokerStartup.SafeCode(ex));Console.Error.WriteLine($"NORTHPASS_BROKER_ERROR stage={stage} code={BrokerStartup.SafeCode(ex)}");int kind=ex switch {UnauthorizedAccessException=>2,InvalidDataException=>3,TimeoutException=>4,_=>1};return 0x4e500000|((int)stage<<8)|kind;}
finally {evidence?.Dispose();}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static EngineInstallationManager Manager(string id,HttpClient http,string appRoot)
{
    using var stream=id=="native"?NativeCatalog.OpenTrustedManifest():FlowsealCatalog.OpenTrustedManifest();
    string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),id=="native"?"Northpass-Native-0.4":"Northpass-Flowseal");
    var previous=new List<EngineManifest>();if(id=="zapret1")foreach(var old in FlowsealCatalog.OpenPreviousTrustedManifests())using(old)previous.Add(EngineManifest.Parse(old));
    return new(root,http,new WindowsInstallationSecurity(),EngineManifest.Parse(stream),previous,
        Path.Combine(appRoot,"engine-payload",id=="native"?"native-offline.zip":"flowseal-offline.zip"),
        id=="native"?NativeEngine.VerifyInstalledVersionAsync:Zapret1Engine.VerifyInstalledVersionAsync,requireOfflinePayload:true);
}
