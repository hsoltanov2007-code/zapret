using System.Net.Http;
using System.Text.Json;
using Northpass.Engine;

namespace Northpass.Services.Installation;

// Trust comes only from manifests embedded in a reviewed Northpass build, never downloaded JSON.
public sealed class EngineInstallationManager : IEngineInstallationManager
{
    private readonly string _root;
    private readonly string? _offlinePayload;
    private readonly bool _requireOfflinePayload;
    private readonly HttpClient _http;
    private readonly IInstallationSecurity _security;
    private readonly EngineManifest _current;
    private readonly Dictionary<string, EngineManifest> _catalog;
    private readonly Func<InstalledEngine, CancellationToken, Task>? _probe;
    private readonly SemaphoreSlim _operations = new(1, 1);
    public string EngineId => _current.EngineId;
    public EngineInstallationManager(string root, HttpClient http, IInstallationSecurity security,
        EngineManifest current, IEnumerable<EngineManifest>? previous = null, string? offlinePayload = null,
        Func<InstalledEngine, CancellationToken, Task>? probe = null, bool requireOfflinePayload = false)
    {
        (_root, _http, _security, _offlinePayload, _probe) = (Path.GetFullPath(root), http, security, offlinePayload, probe);
        _requireOfflinePayload = requireOfflinePayload;
        _current = Freeze(current);
        _catalog = (previous ?? []).Select(Freeze).Append(_current).ToDictionary(m => m.Revision, StringComparer.Ordinal);
        if (_catalog.Values.Any(m => m.EngineId != EngineId)) throw new InvalidDataException("Mixed engine catalog.");
        if (_catalog.Values.Any(m => m.AcquisitionKind == "OfflineBuild") && !requireOfflinePayload)
            throw new InvalidDataException("Native builds can only be installed from this application's bundled offline payload.");
    }
    private static EngineManifest Freeze(EngineManifest manifest)
    {
        manifest.Validate();
        return manifest with { Components = Array.AsReadOnly(manifest.Components.ToArray()) };
    }
    private sealed record Selection(string Current, string? Previous);
    private string StatePath => Path.Combine(_root, "selection.json");
    private Selection? ReadSelection()
    {
        if (!File.Exists(StatePath)) return null;
        SafeArchive.NoLinks(StatePath);
        _security.ValidateFile(StatePath);
        if (new FileInfo(StatePath).Length > 2048) throw new InvalidDataException("Engine selection is invalid.");
        var state = JsonSerializer.Deserialize<Selection>(File.ReadAllText(StatePath)) ?? throw new InvalidDataException("Engine selection is empty.");
        if (!_catalog.ContainsKey(state.Current) || state.Previous is not null && !_catalog.ContainsKey(state.Previous))
            throw new InvalidDataException("Installed engine revision is not trusted by this Northpass build. No downloaded manifest can authorize it.");
        return state;
    }
    private FileStream LockRoot()
    {
        _security.PrepareRoot(_root);
        SafeArchive.NoLinks(_root);
        string file = Path.Combine(_root, ".installation.lock");
        SafeArchive.NoLinks(file);
        try
        {
            if (File.Exists(file)) _security.ValidateFile(file);
            else
            {
                using (new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
                _security.ProtectFile(file);
            }
            return new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) { throw new IOException("Engine installation is in use. Disconnect all Northpass sessions before changing it.", ex); }
    }
    private InstalledEngine Installed(EngineManifest manifest) => new(EngineId, manifest.Revision, manifest.Version,
        Path.GetFullPath(Path.Combine(_root, manifest.Revision, manifest.Executable)), manifest.SourceRevision);

    private async Task<List<FileStream>> VerifyTreeAsync(EngineManifest manifest, CancellationToken token)
    {
        string directory = Path.Combine(_root, manifest.Revision);
        _security.ValidateDirectory(directory);
        var expected = manifest.Components.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leases = new List<FileStream>();
        try
        {
            async Task Walk(string folder)
            {
                SafeArchive.NoLinks(folder); _security.ValidateDirectory(folder);
                foreach (string file in Directory.EnumerateFileSystemEntries(folder))
                {
                    token.ThrowIfCancellationRequested(); SafeArchive.NoLinks(file);
                    if (Directory.Exists(file)) { await Walk(file); continue; }
                    _security.ValidateFile(file);
                    string name = Path.GetRelativePath(directory, file).Replace('\\', '/');
                    if (!expected.TryGetValue(name, out var component) || !seen.Add(name))
                        throw new InvalidDataException("Unexpected file in protected engine directory: " + name);
                    var handle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    leases.Add(handle);
                    if (handle.Length != component.Size || !Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(handle, token)).Equals(component.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Engine component was modified or quarantined: " + name + ". Reinstall the reviewed engine; do not disable Windows security.");
                }
            }
            await Walk(directory);
            if (seen.Count != expected.Count) throw new InvalidDataException("Required engine files are missing. Reinstall the reviewed engine.");
            return leases;
        }
        catch { foreach (var lease in leases) lease.Dispose(); throw; }
    }
    private async Task VerifyAsync(EngineManifest manifest, CancellationToken token)
    {
        foreach (var file in await VerifyTreeAsync(manifest, token)) file.Dispose();
    }

    public async Task<InstalledEngine?> DetectAsync(CancellationToken token = default)
    {
        await _operations.WaitAsync(token);
        try
        {
            if (!Directory.Exists(_root)) return null;
            using var operation = LockRoot();
            var state = ReadSelection();
            if (state is null) return null;
            var manifest = _catalog[state.Current]; await VerifyAsync(manifest, token);
            return Installed(manifest);
        }
        finally { _operations.Release(); }
    }
    public Task<InstalledEngine> EnsureInstalledAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default)
        => InstallAsync(false, progress, token);
    public Task<InstalledEngine> UpdateAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default)
        => InstallAsync(true, progress, token);
    public Task<InstalledEngine> RepairAsync(IProgress<InstallationProgress>? progress = null, CancellationToken token = default)
        => InstallAsync(true, progress, token, repair: true);

    private void ValidateRemovalTree(string directory)
    {
        SafeArchive.NoLinks(directory); _security.ValidateDirectory(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            SafeArchive.NoLinks(entry);
            if (Directory.Exists(entry)) ValidateRemovalTree(entry); else _security.ValidateFile(entry);
        }
    }
    private async Task<InstalledEngine> InstallAsync(bool update, IProgress<InstallationProgress>? progress, CancellationToken token, bool repair = false)
    {
        await _operations.WaitAsync(token);
        string? staging = null;
        string? repairBackup = null;
        string destination = Path.Combine(_root, _current.Revision);
        bool activated = false;
        FileStream? operation = null;
        try
        {
            operation = LockRoot();
            var state = ReadSelection();
            if (state is not null && !repair)
            {
                await VerifyAsync(_catalog[state.Current], token);
                if (!update || state.Current == _current.Revision)
                { progress?.Report(new("Ready", 1, 1)); return Installed(_catalog[state.Current]); }
            }
            if (repair && Directory.Exists(destination))
            {
                // Never execute or bless corrupt bytes. Keep an isolated backup for failure recovery.
                ValidateRemovalTree(destination);
                repairBackup = Path.Combine(_root, ".repair-backup-" + Guid.NewGuid().ToString("N"));
                Directory.Move(destination, repairBackup);
            }
            if (!Directory.Exists(destination))
            {
                staging = Path.Combine(_root, ".stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging); _security.ProtectDirectory(staging);
                string archive = Path.Combine(staging, "acquisition.zip");
                bool offline = _offlinePayload is not null && File.Exists(_offlinePayload);
                if (_requireOfflinePayload && !offline)
                    throw new FileNotFoundException("The bundled network module is missing. Repair the Northpass application installation. No download was attempted.");
                if (offline)
                {
                    SafeArchive.NoLinks(_offlinePayload!);
                    await using var input = new FileStream(_offlinePayload!, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length != _current.OfflineSize) throw new InvalidDataException("Offline engine payload size is invalid. No network fallback was attempted.");
                    await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, token);
                    progress?.Report(new("Verifying", input.Length, input.Length));
                }
                else await DownloadAsync(archive, progress, token);
                await SafeArchive.VerifyFileAsync(archive, offline ? _current.OfflineSize : _current.ArchiveSize,
                    offline ? _current.OfflineSha256 : _current.ArchiveSha256, token);
                progress?.Report(new("Extracting", 0, 1));
                string content = Path.Combine(staging, "content"); Directory.CreateDirectory(content);
                await SafeArchive.ExtractAsync(archive, content, _current, offline, token);
                _security.ProtectDirectory(content);
                token.ThrowIfCancellationRequested();
                Directory.Move(content, destination);
            }
            progress?.Report(new("Verifying", 0, 1));
            await VerifyAsync(_current, token);
            if (_probe is not null) await _probe(Installed(_current), token);
            token.ThrowIfCancellationRequested();
            // Same-volume atomic pointer change is last; failures leave the previous selection intact.
            WriteSelection(new(_current.Revision, state?.Current == _current.Revision ? state.Previous : state?.Current));
            activated = true;
            progress?.Report(new("Ready", 1, 1));
            return Installed(_current);
        }
        finally
        {
            try
            {
                if (repairBackup is not null && Directory.Exists(repairBackup) && !activated)
                {
                    if (Directory.Exists(destination)) { ValidateRemovalTree(destination); Directory.Delete(destination, true); }
                    Directory.Move(repairBackup, destination);
                }
                // Successful repair keeps the quarantined protected backup (a driver image may still be loaded).
                if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true);
            }
            finally { operation?.Dispose(); _operations.Release(); }
        }
    }
    private void WriteSelection(Selection selection)
    {
        SafeArchive.NoLinks(StatePath);
        string temporary = Path.Combine(_root, ".selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, selection); file.Flush(true); }
            _security.ProtectFile(temporary);
            File.Move(temporary, StatePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task DownloadAsync(string destination, IProgress<InstallationProgress>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var response = await _http.GetAsync(_current.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.AbsoluteUri != _current.ArchiveUrl)
                throw new InvalidDataException("Engine acquisition redirected away from the pinned source URL.");
            if (response.Content.Headers.ContentLength is { } length && length != _current.ArchiveSize)
                throw new InvalidDataException("Engine archive size differs from the reviewed archive.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[65536]; long total = 0; int count;
            progress?.Report(new("Downloading", 0, _current.ArchiveSize));
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += count;
                if (total > _current.ArchiveSize) throw new InvalidDataException("Engine archive exceeds its trusted size.");
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                progress?.Report(new("Downloading", total, _current.ArchiveSize));
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Engine download timed out. Check connectivity and try setup again."); }
        catch (HttpRequestException ex) { throw new IOException("Could not download the reviewed engine source archive from GitHub. Use the offline installer or retry. " + ex.Message, ex); }
    }
    public async Task<EngineUpdateStatus> CheckForUpdatesAsync(CancellationToken token = default)
    {
        var installed = await DetectAsync(token);
        bool update = installed is not null && installed.Revision != _current.Revision;
        return new(installed?.Revision ?? "", _current.Revision, update,
            update ? "A newer reviewed revision is included in this Northpass build. Update is optional; the previous verified revision is retained."
                : "The installed engine matches this build's reviewed catalog. New upstream commits require a reviewed Northpass update; no arbitrary latest binaries are trusted.");
    }
    public async Task<InstalledEngine> RollbackAsync(CancellationToken token = default)
    {
        await _operations.WaitAsync(token);
        try
        {
            using var operation = LockRoot();
            var state = ReadSelection() ?? throw new InvalidOperationException("No installed engine.");
            if (state.Previous is null) throw new InvalidOperationException("No previous reviewed engine is available for rollback.");
            var previous = _catalog[state.Previous]; await VerifyAsync(previous, token);
            if (_probe is not null) await _probe(Installed(previous), token);
            token.ThrowIfCancellationRequested();
            WriteSelection(new(state.Previous, state.Current));
            return Installed(previous);
        }
        finally { _operations.Release(); }
    }
    public async Task<IAsyncDisposable> AcquireLaunchLeaseAsync(string executablePath, CancellationToken token = default)
    {
        await _operations.WaitAsync(token);
        FileStream? operation = null;
        try
        {
            operation = LockRoot();
            var state = ReadSelection() ?? throw new InvalidOperationException("Engine setup is required before connecting.");
            var manifest = _catalog[state.Current];
            if (!Path.GetFullPath(executablePath).Equals(Installed(manifest).ExecutablePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Connect accepts only the selected protected engine installation.");
            var files = await VerifyTreeAsync(manifest, token); files.Add(operation); operation = null;
            return new LaunchLease(files);
        }
        finally { operation?.Dispose(); _operations.Release(); }
    }
    private sealed class LaunchLease(List<FileStream> handles) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); return ValueTask.CompletedTask; }
    }
}
