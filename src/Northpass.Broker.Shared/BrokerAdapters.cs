using System.Security.Cryptography;
using System.Text.Json;
using Northpass.Desktop;
using Northpass.Engine;
using Northpass.Engine.Native;
using Northpass.Engine.Zapret1;
using Northpass.Models;
using Northpass.Services;
using Northpass.Services.Installation;
namespace Northpass.Broker;

// Read-only detection runs as the desktop user. All writes and network child
// creation happen in the authenticated elevated helper.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BrokerInstallation(BrokerClient client,string engineId) : IEngineInstallationManager
{
    public string EngineId=>engineId;
    private static EngineManifest Catalog(string id){using var s=id=="native"?NativeCatalog.OpenTrustedManifest():FlowsealCatalog.OpenTrustedManifest();return EngineManifest.Parse(s);}
    public static string Root(string id)=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),id=="native"?"Northpass-Native-0.3":"Northpass-Flowseal");
    private async Task<(InstalledEngine?,List<FileStream>)> ReadAsync(CancellationToken token)
    {
        var leases=new List<FileStream>();string root=Root(engineId);if(!Directory.Exists(root))return(null,leases);
        try
        {
            var security=new WindowsInstallationSecurity();security.ValidateDirectory(root);
            string selection=Path.Combine(root,"selection.json");if(!File.Exists(selection))return(null,leases);security.ValidateFile(selection);
            using(var stream=new FileStream(selection,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length>2048)throw new InvalidDataException("Installation selection exceeds bounds.");
                using var state=await JsonDocument.ParseAsync(stream,cancellationToken:token);
                var manifest=Catalog(engineId);
                string? selected=state.RootElement.GetProperty("Current").GetString();
                if(selected!=manifest.Revision && engineId=="zapret1")
                    foreach(var old in FlowsealCatalog.OpenPreviousTrustedManifests()) using(old) { var trusted=EngineManifest.Parse(old);if(trusted.Revision==selected)manifest=trusted; }
                if(selected!=manifest.Revision)throw new InvalidDataException("Installed revision is not authorized by this application.");
                string directory=Path.Combine(root,manifest.Revision);security.ValidateDirectory(directory);
                var expected=manifest.Components.ToDictionary(c=>c.Path,StringComparer.OrdinalIgnoreCase);
                async Task Walk(string folder)
                {
                    foreach(string path in Directory.EnumerateFileSystemEntries(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        if(Directory.Exists(path)){security.ValidateDirectory(path);await Walk(path);continue;}
                        security.ValidateFile(path);string name=Path.GetRelativePath(directory,path).Replace('\\','/');
                        if(!expected.Remove(name,out var component))throw new InvalidDataException("Unexpected installed component.");
                        var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);leases.Add(file);
                        if(file.Length!=component.Size || !Convert.ToHexString(await SHA256.HashDataAsync(file,token)).Equals(component.Sha256,StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("A required component is missing or modified. Repair installation.");
                    }
                }
                await Walk(directory);if(expected.Count!=0)throw new InvalidDataException("A required component is missing. Repair installation.");
                return(new(engineId,manifest.Revision,manifest.Version,Path.Combine(directory,manifest.Executable),manifest.SourceRevision),leases);
            }
        }
        catch{foreach(var lease in leases)lease.Dispose();throw;}
    }
    public async Task<InstalledEngine?> DetectAsync(CancellationToken token=default){var(installed,leases)=await ReadAsync(token);foreach(var lease in leases)lease.Dispose();return installed;}
    private async Task<InstalledEngine> Operation(string command,IProgress<InstallationProgress>? progress,CancellationToken token)
    {
        progress?.Report(new("Preparing",0,1));var r=await client.RequestAsync(command,engineId,token:token);progress?.Report(new("Ready",1,1));
        return r.Installed??throw new InvalidDataException("Helper did not confirm protected installation.");
    }
    public async Task<InstalledEngine> EnsureInstalledAsync(IProgress<InstallationProgress>? progress=null,CancellationToken token=default)
        => await DetectAsync(token)??await Operation("INSTALL",progress,token);
    public Task<InstalledEngine> RepairAsync(IProgress<InstallationProgress>? progress=null,CancellationToken token=default)=>Operation("REPAIR",progress,token);
    public Task<InstalledEngine> UpdateAsync(IProgress<InstallationProgress>? progress=null,CancellationToken token=default)=>Operation("INSTALL",progress,token);
    public Task<InstalledEngine> RollbackAsync(CancellationToken token=default)=>Operation("ROLLBACK",null,token);
    public async Task<EngineUpdateStatus> CheckForUpdatesAsync(CancellationToken token=default)
    {var installed=await DetectAsync(token);var manifest=Catalog(engineId);return new(installed?.Revision??"",manifest.Revision,installed?.Revision!=manifest.Revision,"Only this application's reviewed offline components are authorized.");}
    public async Task<IAsyncDisposable> AcquireLaunchLeaseAsync(string executablePath,CancellationToken token=default)
    {
        var(installed,leases)=await ReadAsync(token);
        if(installed?.ExecutablePath!=executablePath){foreach(var lease in leases)lease.Dispose();throw new UnauthorizedAccessException("Unreviewed launch path.");}
        return new ReadLease(leases);
    }
    private sealed class ReadLease(List<FileStream> files):IAsyncDisposable{public ValueTask DisposeAsync(){foreach(var file in files)file.Dispose();return ValueTask.CompletedTask;}}
}
public sealed class BrokerEngine : IDpiEngine,IEnginePerformanceProvider
{
    private readonly BrokerClient _client;private readonly string _id;private readonly DataListStore? _lists;
    private EngineStatus _status=new(EngineState.Disconnected);private bool _started;
    public BrokerEngine(BrokerClient client,string id,DataListStore? lists=null){(_client,_id,_lists)=(client,id,lists);_client.LogReceived+=OnLog;}
    public EngineDescriptor Descriptor=>_id=="native"?NativeEngine.Metadata:Zapret1Engine.Metadata;
    public event Action<string>? LogReceived;public event Action<EngineStatus>? StatusChanged;
    private void OnLog(string line)=>LogReceived?.Invoke(line);
    private void Set(EngineStatus status){_status=status;StatusChanged?.Invoke(status);}
    public Task<ValidationResult> ValidateConfigurationAsync(EngineConfiguration c,CancellationToken cancellationToken=default)
    {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult(_id=="native"?NativeCatalog.Validate(c):Zapret1ConfigurationValidator.Validate(c));}
    public async Task StartAsync(EngineConfiguration configuration,CancellationToken token=default)
    {
        if(_started)throw new InvalidOperationException("Disconnect the owned session first.");
        var validation=await ValidateConfigurationAsync(configuration,token);if(!validation.IsValid)throw new InvalidDataException(validation.Summary);
        Set(new(EngineState.Connecting));var p=configuration.Profile;
        try
        {
            Dictionary<string,string>? lists=null;
            if(p.ListBindings.Count>0){lists=new();foreach(var(slot,id)in p.ListBindings)lists[slot]=(_lists??throw new InvalidDataException("Data-only list store missing.")).Read(id,slot is "general" or "excluded-hosts"?DataListKind.Hosts:DataListKind.IpSet);}
            var r=await _client.RequestAsync("START",_id,p.StrategyId,p.Native?.LoopbackPort,p.Native?.Transport??"both",p.GameTcpPorts,p.GameUdpPorts,lists,token);
            _started=true;Set(r.Status??throw new InvalidDataException("Network component status missing."));
        }
        catch(Exception ex){Set(new(EngineState.Error,Error:ex.Message));throw;}
    }
    public async Task StopAsync(CancellationToken token=default)
    {
        if(!_started){Set(new(EngineState.Disconnected));return;}
        try{var r=await _client.RequestAsync("STOP",_id,token:token);_started=false;Set(r.Status??new(EngineState.Disconnected));}
        catch(Exception ex){_started=false;Set(new(EngineState.Error,Error:ex.Message));throw;}
    }
    public async Task RestartAsync(EngineConfiguration c,CancellationToken token=default){await StopAsync(token);await StartAsync(c,token);}
    public async Task<EngineStatus> GetStatusAsync(CancellationToken token=default)
    {
        if(_started)try{var r=await _client.RequestAsync("STATUS",_id,token:token);_status=r.Status??throw new InvalidDataException("Missing component status.");}
        catch(Exception ex) when (ex is not OperationCanceledException){_status=new(EngineState.Error,Error:ex.Message);}
        return _status;
    }
    public async Task<EnginePerformance?> GetPerformanceAsync(CancellationToken cancellationToken=default)=>_started?(await _client.RequestAsync("METRICS",_id,token:cancellationToken)).Performance:null;
    public async ValueTask DisposeAsync(){try{await StopAsync();}finally{_client.LogReceived-=OnLog;}}
}
