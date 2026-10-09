using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Zapret1;
using Northpass.Models;
using Northpass.Services;
using Northpass.Services.Installation;

namespace Northpass.Windows.Tests;

public sealed class TelegramWindowsTests
{
    private static string Repository() { for(var d=new DirectoryInfo(AppContext.BaseDirectory);d!=null;d=d.Parent) if(File.Exists(Path.Combine(d.FullName,"Northpass.sln")))return d.FullName;throw new DirectoryNotFoundException(); }
    [Fact]
    public async Task RealDriverSegmentsContainedTelegramTcpOnlyAndPreservesSyntheticStream()
    {
        string repo=Repository(), root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Northpass-telegram-test");
        using var manifest=FlowsealCatalog.OpenTrustedManifest();using var http=new HttpClient();var security=new WindowsInstallationSecurity();
        var manager=new EngineInstallationManager(root,http,security,EngineManifest.Parse(manifest),offlinePayload:Path.Combine(repo,"dist/Northpass/engine-payload/flowseal-offline.zip"),probe:Zapret1Engine.VerifyInstalledVersionAsync,requireOfflinePayload:true);
        var installed=await manager.EnsureInstalledAsync();await using var lease=await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath);
        using var api=new FlowsealCaptureWindowsTests.Divert(Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!,"WinDivert.dll"));
        string target=TelegramEndpoints.Bootstrap[0].Address, control="203.0.113.88";
        string scope=$"outbound and ip and ip.SrcAddr == 198.51.100.1 and tcp.SrcPort == 55002 and tcp.DstPort == 443 and (ip.DstAddr == {target} or ip.DstAddr == {control})";
        // Never expose synthetic packets to a public endpoint. Guard is live
        // BEFORE engine/injector; source/ports are reserved solely for this test.
        using var guard=api.Handle(scope,0,-1000,2);using var sniff=api.Handle(scope,0,-999,5);using var injector=api.Handle("false",0,1000,8);
        string user=Path.Combine(Path.GetTempPath(),"northpass-tg-"+Guid.NewGuid().ToString("N"));
        var provider=new ProtectedEngineDataProvider(new DataListStore(user),Path.Combine(root,"data"),security);
        await using var data=await provider.PrepareAsync(FlowsealCatalog.Profile(FlowsealCatalog.Find("general")!));
        var info=Zapret1Engine.CreateStartInfo(new(installed.ExecutablePath,FlowsealCatalog.Profile(FlowsealCatalog.Find("general")!)),data);
        foreach(string arg in info.ArgumentList.Where(a=>a.StartsWith("--wf-",StringComparison.Ordinal)).ToArray())info.ArgumentList.Remove(arg);
        info.ArgumentList.Insert(0,"--wf-raw="+scope);info.RedirectStandardOutput=info.RedirectStandardError=true;
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process=Process.Start(info)!;var errors=process.StandardError.ReadToEndAsync();
        var output=Task.Run(async()=> { while(await process.StandardOutput.ReadLineAsync() is {} line)if(line==FlowsealCaptureObserver.ReadyLine)ready.TrySetResult(); });
        var transformed=new ConcurrentQueue<(uint Sequence,byte[] Payload)>();var unchanged=new ConcurrentQueue<byte[]>();
        var receive=Task.Run(()=> { while(api.Receive(sniff.Value,out var p,out _)) {
            if(p.Length<40)continue;int ip=(p[0]&15)*4,tcp=(p[ip+12]>>4)*4,offset=ip+tcp;
            if(offset>=p.Length)continue;
            if(p.AsSpan(16,4).SequenceEqual(IPAddress.Parse(target).GetAddressBytes()))transformed.Enqueue((BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(ip+4)),p.AsSpan(offset).ToArray()));
            else unchanged.Enqueue(p.AsSpan(offset).ToArray());
        }});
        byte[] pattern=Enumerable.Range(0,80).Select(i=>(byte)(i+1)).ToArray();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            foreach(string destination in new[]{target,control}) {
                foreach(bool syn in new[]{true,false}) {
                    byte[] packet=new byte[40+(syn?0:pattern.Length)];packet[0]=0x45;packet[8]=64;packet[9]=6;
                    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2),(ushort)packet.Length);
                    IPAddress.Parse("198.51.100.1").GetAddressBytes().CopyTo(packet,12);IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet,16);
                    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20),55002);BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22),443);
                    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24),syn?1000u:1001u);BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28),syn?0u:2001u);
                    packet[32]=0x50;packet[33]=(byte)(syn?2:0x18);packet[34]=0xff;packet[35]=0xff;if(!syn)pattern.CopyTo(packet,40);
                    byte[] address=new byte[80];address[10]=2;Assert.True(api.Checksum(packet,(uint)packet.Length,address,0));
                    Assert.True(api.Send(injector.Value,packet,(uint)packet.Length,out uint sent,address));Assert.Equal((uint)packet.Length,sent);
                }
            }
            var timer=Stopwatch.StartNew();while((transformed.Count<2 || unchanged.IsEmpty)&&timer.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(50);
            Assert.Equal(2,transformed.Count);var segments=transformed.OrderBy(s=>s.Sequence).ToArray();
            Assert.Equal(1001u,segments[0].Sequence);Assert.Single(segments[0].Payload);Assert.Equal(1002u,segments[1].Sequence);
            Assert.Equal(pattern,segments.SelectMany(s=>s.Payload).ToArray());Assert.Single(unchanged);Assert.Equal(pattern,unchanged.Single());
            Assert.False(process.HasExited);
        }
        finally { if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();await output;sniff.Shutdown();await receive.WaitAsync(TimeSpan.FromSeconds(5));if(Directory.Exists(user))Directory.Delete(user,true); }
        Directory.CreateDirectory(Path.Combine(repo,"TestResults"));File.WriteAllText(Path.Combine(repo,"TestResults/engine-telegram-tcp-lab.txt"),
            "ACTUAL Windows WinDivert, contained synthetic TCP: exact official bootstrap destination matched Telegram rule; opaque first data yielded 2 ordered non-overlapping segments with exact reconstruction. Non-Telegram control yielded 1 unchanged payload. All packets DROP-contained before NIC. Owned process/receive cleaned up. No Telegram server, account login, messages, IPv6 end-to-end or ISP bypass tested. No raw packet files retained.");
    }
}
