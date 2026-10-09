using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Northpass.Desktop;
using Northpass.Engine.Native;
using Northpass.Models;
using Northpass.Services.Installation;
namespace Northpass.Windows.Tests;

public sealed class NativeFaultWindowsTests
{
    [Fact]
    public async Task ActualScopedDriverFaultsAndBackpressureAreReportedAndCleanedUp()
    {
        string repo=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(repo,"Northpass.sln")))repo=Directory.GetParent(repo)!.FullName;
        using var catalog=NativeCatalog.OpenTrustedManifest();using var http=new HttpClient();
        var manager=new EngineInstallationManager(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Northpass-Native-0.3"),http,new WindowsInstallationSecurity(),EngineManifest.Parse(catalog),
            offlinePayload:Path.Combine(repo,"dist/native/native-offline.zip"),probe:NativeEngine.VerifyInstalledVersionAsync,requireOfflinePayload:true);
        var installed=await manager.EnsureInstalledAsync();var evidence=new List<string>();
        foreach(string fault in new[]{"driver-open","send","observation-delay","crash"})
        {
            using var socket=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));socket.Client.ReceiveBufferSize=1024*1024;
            using var sender=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));int port=((IPEndPoint)socket.Client.LocalEndPoint!).Port;Assert.InRange(port,49152,65535);
            var configuration=new EngineConfiguration(installed.ExecutablePath,fault=="driver-open"?NativeCatalog.Idle():NativeCatalog.Loopback(port,"udp"));
            var info=NativeEngine.CreateStartInfo(configuration);info.RedirectStandardInput=true;info.RedirectStandardOutput=true;info.RedirectStandardError=true;
            foreach(var arg in new[]{"--test-capability","--test-fault",fault})info.ArgumentList.Add(arg);
            await using var lease=await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath);
            using var process=Process.Start(info)!;
            var lines=new List<string>();var errors=process.StandardError.ReadToEndAsync();
            try
            {
                if(fault=="driver-open")
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Assert.NotEqual(0,process.ExitCode);
                    Assert.Contains("Injected scoped driver initialization failure",await errors);evidence.Add("driver-open: clear nonzero initialization error; owned process exited");continue;
                }
                Assert.Equal("NORTHPASS_READY protocol=1",await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                var output=Task.Run(async()=>{string? line;while((line=await process.StandardOutput.ReadLineAsync())!=null)lines.Add(line);});
                byte[] bytes=Enumerable.Range(0,128).Select(i=>(byte)i).ToArray();
                if(fault=="observation-delay")
                {
                    var reception=Task.Run(async()=>{for(int i=0;i<128;i++)Assert.Equal(bytes,(await socket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10))).Buffer);});
                    for(int i=0;i<128;i++)await sender.SendAsync(bytes,new IPEndPoint(IPAddress.Loopback,port));
                    await reception;await process.StandardInput.WriteLineAsync("STOP");await process.StandardInput.FlushAsync();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));await output;Assert.Equal(0,process.ExitCode);
                    string line=lines.Last(l=>l.StartsWith("NORTHPASS_METRICS "));var metrics=NativeMetrics.Parse(line);
                    Assert.True(metrics!.Backpressure>0,line);Assert.Equal(0UL,metrics.KnownDropped);Assert.True(metrics.KernelLossUnknown);Assert.InRange(metrics.QueuePeak,1UL,8UL);
                    evidence.Add("queue saturation: 128 original datagrams unchanged; bounded queue; observation skips reported; kernel losses unknown. "+line);
                }
                else
                {
                    await sender.SendAsync(bytes,new IPEndPoint(IPAddress.Loopback,port));await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));await output;
                    Assert.NotEqual(0,process.ExitCode);
                    if(fault=="send") {string line=lines.Last(l=>l.StartsWith("NORTHPASS_METRICS "));var metrics=NativeMetrics.Parse(line);Assert.True(metrics!.KnownDropped>=1 && metrics.FatalErrors>=1 && metrics.KernelLossUnknown);evidence.Add("reinjection failure: actual scoped captured packet was deliberately not sent; known drop/fatal reported. "+line);}
                    else {Assert.Equal(91,process.ExitCode);evidence.Add("worker crash: real TerminateProcess(91), scoped packet delivery/loss unknown; process exited, ownership released");}
                }
            }
            finally {if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}}
        }
        // Reopen the real driver after all failure modes: no stale owned mutex/handle.
        await using var recovery=new NativeEngine(manager);await recovery.StartAsync(new(installed.ExecutablePath,NativeCatalog.Idle()));await recovery.StopAsync();
        Directory.CreateDirectory(Path.Combine(repo,"TestResults"));File.WriteAllLines(Path.Combine(repo,"TestResults/engine-native-faults.txt"),evidence);
    }
}
