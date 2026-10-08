using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Northpass.Services.Installation;

namespace Northpass.Tests;

public sealed class EngineInstallationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "northpass-install-tests-" + Guid.NewGuid().ToString("N"));
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private sealed class TestSecurity : IInstallationSecurity
    {
        public void PrepareRoot(string root) { SafeArchive.NoLinks(root); Directory.CreateDirectory(root); }
        public void ProtectDirectory(string path) => SafeArchive.NoLinks(path);
        public void ValidateDirectory(string path) { SafeArchive.NoLinks(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path); }
        public void ProtectFile(string path) => SafeArchive.NoLinks(path);
        public void ValidateFile(string path) => SafeArchive.NoLinks(path);
    }
    private sealed class Handler(byte[] archive, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests;
        public Uri? LastUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; LastUrl = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(archive), RequestMessage = request });
        }
    }
    private static byte[] Zip(params (string Name, byte[] Data, int Attributes)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name); entry.ExternalAttributes = file.Attributes;
                using var output = entry.Open(); output.Write(file.Data);
            }
        return stream.ToArray();
    }
    private static (EngineManifest Manifest, byte[] Archive) Fixture(char revision = 'a', string content = "test engine")
    {
        byte[] exe = Encoding.UTF8.GetBytes(content), dll = Encoding.UTF8.GetBytes("trusted runtime");
        byte[] archive = Zip(("engine/winws.exe", exe, 0), ("engine/runtime.dll", dll, 0));
        string commit = new(revision, 40);
        return (new("zapret1", commit, "1.0.0", "winws.exe", "https://codeload.github.com/test/engine/zip/" + commit,
            Hash(archive), archive.Length, "", Hash(archive), archive.Length,
            [new("engine/winws.exe", "winws.exe", exe.Length, Hash(exe)), new("engine/runtime.dll", "runtime.dll", dll.Length, Hash(dll))]), archive);
    }
    private EngineInstallationManager Manager(HttpClient http, EngineManifest manifest, IEnumerable<EngineManifest>? previous = null,
        string? offline = null, Func<Northpass.Engine.InstalledEngine, CancellationToken, Task>? probe = null)
        => new(Path.Combine(_directory, "protected"), http, new TestSecurity(), manifest, previous, offline, probe);
    private string State => Path.Combine(_directory, "protected/selection.json");

    [Fact]
    public async Task NativeOfflineBuildRequiresBundledVerifiedBytesAndNeverDownloads()
    {
        var fixture = Fixture(); string hash = Hash(fixture.Archive);
        var native = fixture.Manifest with { EngineId = "native", AcquisitionKind = "OfflineBuild", SourceRevision = new string('b', 40), Revision = hash[..40], ArchiveUrl = "" };
        native.Validate();
        using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        Assert.Throws<InvalidDataException>(() => Manager(http, native));
        Directory.CreateDirectory(_directory); var payload = Path.Combine(_directory, "native.zip");
        var manager = new EngineInstallationManager(Path.Combine(_directory, "native"), http, new TestSecurity(), native, offlinePayload: payload, requireOfflinePayload: true);
        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.EnsureInstalledAsync()); Assert.Equal(0, handler.Requests);
        await File.WriteAllBytesAsync(payload, fixture.Archive);
        var installed = await manager.EnsureInstalledAsync(); Assert.Equal(installed, await manager.EnsureInstalledAsync()); Assert.Equal(0, handler.Requests);
        await using (var lease = await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath))
        {
            if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => File.Open(installed.ExecutablePath, FileMode.Open, FileAccess.Write, FileShare.Read));
            else Assert.NotNull(lease); // Unix FileShare is not mandatory write denial.
        }
        Assert.Throws<InvalidDataException>(() => (native with { EngineId = "zapret1" }).Validate());
        Assert.Throws<InvalidDataException>(() => (native with { ArchiveUrl = "https://example.org/native.zip" }).Validate());
        Assert.Throws<InvalidDataException>(() => (native with { OfflineSha256 = "short" }).Validate());
        Assert.Throws<InvalidDataException>(() => (native with { AcquisitionKind = "Latest" }).Validate());
        var broken = fixture.Archive.ToArray(); broken[10] ^= 1; await File.WriteAllBytesAsync(payload, broken);
        Directory.Delete(Path.Combine(_directory, "native"), true);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.EnsureInstalledAsync()); Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task NestedExecutablePathsAreCanonicalizedBeforeAcquiringTheProtectedLease()
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var manifest = fixture.Manifest with { Executable = "bin/winws.exe", Components = fixture.Manifest.Components.Select(c => c with { Path = "bin/" + c.Path }).ToArray() };
        var manager = Manager(http, manifest); var installed = await manager.EnsureInstalledAsync();
        Assert.Equal(Path.GetFullPath(installed.ExecutablePath), installed.ExecutablePath);
        await using var lease = await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath);
        Assert.True(File.Exists(installed.ExecutablePath));
    }
    [Fact]
    public async Task DownloadVerifiesAndReusesInstalledComponents()
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var manager = Manager(http, fixture.Manifest); Assert.Null(await manager.DetectAsync());
        var progress = new RecordingProgress();
        var installed = await manager.EnsureInstalledAsync(progress);
        Assert.True(File.Exists(installed.ExecutablePath)); Assert.Equal(fixture.Manifest.Revision, installed.Revision);
        Assert.Equal(installed, await manager.DetectAsync()); Assert.Equal(installed, await manager.EnsureInstalledAsync());
        Assert.Equal(1, handler.Requests); Assert.Equal(fixture.Manifest.ArchiveUrl, handler.LastUrl!.AbsoluteUri);
        Assert.Contains(progress.Values, p => p.Phase == "Downloading" && p.Percent == 100);
        Assert.Equal("Ready", progress.Values[^1].Phase);
    }
    private sealed class RecordingProgress : IProgress<Northpass.Engine.InstallationProgress>
    {
        public List<Northpass.Engine.InstallationProgress> Values { get; } = [];
        public void Report(Northpass.Engine.InstallationProgress value) => Values.Add(value);
    }
    [Fact]
    public async Task PackagedApplicationNeverDownloadsWhenItsBundledPayloadIsMissing()
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var manager = new EngineInstallationManager(Path.Combine(_directory, "protected"), http, new TestSecurity(),
            fixture.Manifest, offlinePayload: Path.Combine(_directory, "missing.zip"), requireOfflinePayload: true);
        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.EnsureInstalledAsync());
        Assert.Equal(0, handler.Requests); Assert.False(File.Exists(State));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(State)!, ".stage-*"));
    }
    [Fact]
    public async Task OfflineInstallMakesNoNetworkRequests()
    {
        var fixture = Fixture(); Directory.CreateDirectory(_directory);
        string offline = Path.Combine(_directory, "offline.zip"); await File.WriteAllBytesAsync(offline, fixture.Archive);
        using var handler = new Handler([], HttpStatusCode.ServiceUnavailable); using var http = new HttpClient(handler);
        var manager = Manager(http, fixture.Manifest, offline: offline);
        Assert.True(File.Exists((await manager.EnsureInstalledAsync()).ExecutablePath)); Assert.Equal(0, handler.Requests);
    }
    [Fact]
    public async Task TamperedOfflinePayloadFailsClosedWithoutNetworkFallback()
    {
        var fixture = Fixture(); Directory.CreateDirectory(_directory);
        string offline = Path.Combine(_directory, "offline.zip"); var corrupt = fixture.Archive.ToArray(); corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(offline, corrupt);
        using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager(http, fixture.Manifest, offline: offline).EnsureInstalledAsync());
        Assert.Equal(0, handler.Requests); Assert.False(File.Exists(State));
    }
    [Fact]
    public async Task IncorrectArchiveHashNeverActivatesEngine()
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var manifest = fixture.Manifest with { ArchiveSha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager(http, manifest).EnsureInstalledAsync());
        Assert.False(File.Exists(State)); Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(State)!, ".stage-*"));
    }
    [Fact]
    public async Task EveryComponentHashIsCheckedEvenWhenArchiveHashIsValid()
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var components = fixture.Manifest.Components.ToArray(); components[1] = components[1] with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager(http, fixture.Manifest with { Components = components }).EnsureInstalledAsync());
        Assert.False(File.Exists(State));
    }
    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("engine/../outside")]
    [InlineData("engine\\outside")]
    [InlineData("engine/file:stream")]
    [InlineData("engine/CON.txt")]
    [InlineData("engine/space.")]
    public async Task UnsafeNonSelectedArchiveEntriesAreRejected(string name)
    {
        var fixture = Fixture(); byte[] zip = Zip((name, [1], 0));
        var manifest = fixture.Manifest with { ArchiveSha256 = Hash(zip), ArchiveSize = zip.Length };
        using var handler = new Handler(zip); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager(http, manifest).EnsureInstalledAsync());
        Assert.False(File.Exists(State)); Assert.False(File.Exists(Path.Combine(_directory, "outside")));
    }
    [Theory]
    [InlineData(0xa000)]
    [InlineData(0x6000)]
    public async Task ArchiveLinksAndSpecialFilesAreRejected(int kind)
    {
        var fixture = Fixture(); byte[] zip = Zip(("engine/link", [1], kind << 16));
        using var handler = new Handler(zip); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager(http, fixture.Manifest with { ArchiveSha256 = Hash(zip), ArchiveSize = zip.Length }).EnsureInstalledAsync());
    }
    [Fact]
    public async Task DuplicateCaseInsensitiveArchivePathsAreRejected()
    {
        var fixture = Fixture(); byte[] zip = Zip(("engine/file", [1], 0), ("ENGINE/FILE", [2], 0));
        using var handler = new Handler(zip); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager(http, fixture.Manifest with { ArchiveSha256 = Hash(zip), ArchiveSize = zip.Length }).EnsureInstalledAsync());
    }
    [Fact]
    public async Task MissingRequiredComponentsCannotActivate()
    {
        var fixture = Fixture(); byte[] zip = Zip(("unrelated", [1], 0));
        using var handler = new Handler(zip); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager(http, fixture.Manifest with { ArchiveSha256 = Hash(zip), ArchiveSize = zip.Length }).EnsureInstalledAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledTamperingOrExtraDllFailsDetectionAndLaunch(bool extra)
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var manager = Manager(http, fixture.Manifest); var engine = await manager.EnsureInstalledAsync();
        await File.WriteAllTextAsync(extra ? Path.Combine(Path.GetDirectoryName(engine.ExecutablePath)!, "ole32.dll") : engine.ExecutablePath, "untrusted");
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DetectAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.AcquireLaunchLeaseAsync(engine.ExecutablePath));
    }
    [Fact]
    public async Task LaunchLeasePreventsConcurrentInstallationAndRejectsAlternateExecutable()
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var manager = Manager(http, fixture.Manifest); var engine = await manager.EnsureInstalledAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.AcquireLaunchLeaseAsync(Path.Combine(_directory, "winws.exe")));
        await using (var lease = await manager.AcquireLaunchLeaseAsync(engine.ExecutablePath))
            await Assert.ThrowsAsync<IOException>(() => manager.UpdateAsync());
        Assert.Equal(engine, await manager.DetectAsync());
    }
    [Fact]
    public async Task ReviewedUpdateAndRollbackPreserveBothVerifiedVersions()
    {
        var old = Fixture(); using var oldHandler = new Handler(old.Archive); using var oldHttp = new HttpClient(oldHandler);
        var original = await Manager(oldHttp, old.Manifest).EnsureInstalledAsync();
        var newer = Fixture('b', "new engine"); using var handler = new Handler(newer.Archive); using var http = new HttpClient(handler);
        var manager = Manager(http, newer.Manifest, [old.Manifest]);
        Assert.True((await manager.CheckForUpdatesAsync()).CanUpdate);
        Assert.Equal(original, await manager.EnsureInstalledAsync()); Assert.Equal(0, handler.Requests);
        var updated = await manager.UpdateAsync(); Assert.Equal(newer.Manifest.Revision, updated.Revision);
        Assert.True(File.Exists(original.ExecutablePath)); Assert.Equal(original, await manager.RollbackAsync());
        Assert.Equal(updated, await manager.RollbackAsync()); Assert.Equal(1, handler.Requests);
    }
    [Fact]
    public async Task FailedUpdateProbeLeavesPreviousVersionSelected()
    {
        var old = Fixture(); using var oldHandler = new Handler(old.Archive); using var oldHttp = new HttpClient(oldHandler);
        var original = await Manager(oldHttp, old.Manifest).EnsureInstalledAsync();
        var newer = Fixture('b', "new engine"); using var handler = new Handler(newer.Archive); using var http = new HttpClient(handler);
        var manager = Manager(http, newer.Manifest, [old.Manifest], probe: (_, _) => throw new IOException("version mismatch"));
        await Assert.ThrowsAsync<IOException>(() => manager.UpdateAsync()); Assert.Equal(original, await manager.DetectAsync());
    }
    [Fact]
    public async Task ExplicitRepairRestoresReviewedBytesAndQuarantinesCorruption()
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var manager = Manager(http, fixture.Manifest); var engine = await manager.EnsureInstalledAsync();
        await File.WriteAllTextAsync(engine.ExecutablePath, "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DetectAsync());
        Assert.Equal(engine, await manager.RepairAsync()); Assert.Equal(engine, await manager.DetectAsync());
        var backup = Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(State)!, ".repair-backup-*"));
        Assert.Equal("corrupt", await File.ReadAllTextAsync(Path.Combine(backup, "winws.exe")));
        Assert.Equal(2, handler.Requests);
    }
    [Fact]
    public async Task FailedRepairRestoresPriorDirectoryAndSelectionWithoutExecutingCorruptBytes()
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var original = Manager(http, fixture.Manifest); var installed = await original.EnsureInstalledAsync();
        await File.WriteAllTextAsync(installed.ExecutablePath, "corrupt"); string selection = await File.ReadAllTextAsync(State);
        using var failedHandler = new Handler([], HttpStatusCode.ServiceUnavailable); using var failedHttp = new HttpClient(failedHandler);
        await Assert.ThrowsAsync<IOException>(() => Manager(failedHttp, fixture.Manifest).RepairAsync());
        Assert.Equal("corrupt", await File.ReadAllTextAsync(installed.ExecutablePath)); Assert.Equal(selection, await File.ReadAllTextAsync(State));
        await Assert.ThrowsAsync<InvalidDataException>(() => original.DetectAsync());
        Assert.Equal(installed, await original.RepairAsync());
    }
    [Fact]
    public async Task TamperedRollbackTargetCannotReplaceHealthySelection()
    {
        var old = Fixture(); using var oldHandler = new Handler(old.Archive); using var oldHttp = new HttpClient(oldHandler);
        var original = await Manager(oldHttp, old.Manifest).EnsureInstalledAsync();
        var newer = Fixture('b', "new engine"); using var handler = new Handler(newer.Archive); using var http = new HttpClient(handler);
        var manager = Manager(http, newer.Manifest, [old.Manifest]); var updated = await manager.UpdateAsync();
        await File.WriteAllTextAsync(original.ExecutablePath, "corrupt");
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.RollbackAsync());
        Assert.Equal(updated, await manager.DetectAsync());
    }
    [Fact]
    public async Task UnknownRevisionIsNotAuthorizedByEditableSelection()
    {
        var fixture = Fixture(); using var handler = new Handler(fixture.Archive); using var http = new HttpClient(handler);
        var manager = Manager(http, fixture.Manifest); await manager.EnsureInstalledAsync();
        await File.WriteAllTextAsync(State, "{\"Current\":\"" + new string('f', 40) + "\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DetectAsync());
    }
    [Fact]
    public async Task DownloadFailuresAndCancellationNeverActivate()
    {
        var fixture = Fixture(); using var handler = new Handler([], HttpStatusCode.ServiceUnavailable); using var http = new HttpClient(handler);
        var manager = Manager(http, fixture.Manifest); await Assert.ThrowsAsync<IOException>(() => manager.EnsureInstalledAsync());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.EnsureInstalledAsync(token: cancellation.Token));
        Assert.False(File.Exists(State));
    }
    [Fact]
    public void DanglingLinksAndLinkedAncestorsAreRejected()
    {
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, "dangling");
        File.CreateSymbolicLink(file, Path.Combine(_directory, "missing"));
        Assert.Throws<IOException>(() => SafeArchive.NoLinks(file));
        string outside = Path.Combine(_directory, "outside"); Directory.CreateDirectory(outside);
        string folder = Path.Combine(_directory, "linked-folder"); Directory.CreateSymbolicLink(folder, outside);
        Assert.Throws<IOException>(() => SafeArchive.NoLinks(Path.Combine(folder, "future-file")));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
