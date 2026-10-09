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
        if(mode=="repair")await native.RepairAsync();
        var installed=await native.EnsureInstalledAsync();
        using var receiver=new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback,0));
        using var sender=new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback,0));
        int port=((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        if(port is <49152 or >65535)throw new IOException("A dedicated reserved loopback socket is required.");
        await using var engine=new BrokerEngine(client,"native");
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
        await engine.StopAsync();
        if(mode=="flowseal")
        {
            if(!(await client.RequestAsync("STATUS")).NoTrafficTest)throw new IOException("Flowseal acceptance requires verified no-traffic test composition.");
            var fallback=await flowseal.EnsureInstalledAsync();
            await using var checkedEngine=new BrokerEngine(client,"zapret1");
            await checkedEngine.StartAsync(new(fallback.ExecutablePath,FlowsealCatalog.Profile(FlowsealCatalog.Strategies[0])));
            if((await checkedEngine.GetStatusAsync()).State!=EngineState.Active)throw new IOException("Broker Flowseal readiness failed.");
            await checkedEngine.StopAsync(); // only CI --test-no-traffic bootstrap permits this acceptance path
        }
        if(mode=="replay")await client.VerifyReplayRejectionForAcceptanceAsync();
        var result=new{UiAdministrator=false,AuthenticatedElevatedWorker=true,Mode=mode,Ipv6UnchangedDatagrams=64,Metrics=metrics,
            Scope="Dedicated loopback only; Flowseal check requires explicit filter=false test bootstrap. No DPI bypass/hardware certification.",Logs=logs};
        int output=Array.IndexOf(args,"--evidence");if(output>=0)await File.WriteAllTextAsync(args[output+1],JsonSerializer.Serialize(result));
    }
}
