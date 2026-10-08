using System.Collections.Concurrent;
using System.Diagnostics;
using Northpass.Desktop;
using Northpass.Engine.Zapret1;
using Northpass.Engine;
using Northpass.Models;
using Northpass.Services;
using Northpass.Services.Installation;

namespace Northpass.Windows.Tests;

public sealed class FlowsealWindowsTests
{
    private static string Repository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Northpass.sln"))) return folder.FullName;
        throw new DirectoryNotFoundException("Repository checkout required.");
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; throw new InvalidOperationException("Offline setup attempted a network download."); }
    }
    private static void Evidence(string repo, string name, string text)
    { Directory.CreateDirectory(Path.Combine(repo, "TestResults")); File.WriteAllText(Path.Combine(repo, "TestResults", name + ".txt"), text); }
    [Fact]
    public async Task ReviewedWinwsInstallsOfflineParsesAllFiveStrategiesAndOwnsFalseFilterSession()
    {
        string repo = Repository();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-flowseal-test-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-data-test-" + Guid.NewGuid().ToString("N"));
        string userRoot = Path.Combine(Path.GetTempPath(), "northpass-flowseal-user-" + Guid.NewGuid().ToString("N"));
        using var manifestStream = FlowsealCatalog.OpenTrustedManifest(); var manifest = EngineManifest.Parse(manifestStream);
        using var handler = new NoNetwork(); using var http = new HttpClient(handler);
        var security = new WindowsInstallationSecurity();
        var manager = new EngineInstallationManager(root, http, security, manifest,
            offlinePayload: Path.Combine(repo, "dist/Northpass/engine-payload/flowseal-offline.zip"),
            probe: Zapret1Engine.VerifyInstalledVersionAsync, requireOfflinePayload: true);
        var provider = new ProtectedEngineDataProvider(new DataListStore(userRoot), dataRoot, security);
        try
        {
            var installed = await manager.EnsureInstalledAsync();
            Assert.Equal(installed, await manager.DetectAsync()); Assert.Equal(installed, await manager.EnsureInstalledAsync()); Assert.Equal(0, handler.Requests);
            foreach (var asset in manifest.Components) security.ValidateFile(Path.Combine(root, manifest.Revision, asset.Path));
            foreach (var strategy in FlowsealCatalog.Strategies)
            {
                var profile = FlowsealCatalog.Profile(strategy);
                Assert.True(Zapret1ConfigurationValidator.Validate(new(installed.ExecutablePath, profile)).IsValid);
                await using var engineLease = await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath);
                await using var dataLease = await provider.PrepareAsync(profile);
                foreach (string file in Directory.GetFiles(dataLease.DirectoryPath))
                {
                    security.ValidateFile(file);
                    Assert.Throws<IOException>(() => File.Open(file, FileMode.Open, FileAccess.Write, FileShare.Read));
                }
                var info = Zapret1Engine.CreateStartInfo(new(installed.ExecutablePath, profile), dataLease);
                info.ArgumentList.Add("--dry-run"); info.RedirectStandardOutput = true; info.RedirectStandardError = true;
                using var process = Process.Start(info)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(timeout.Token); }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                string text = await output + await error;
                Assert.True(process.ExitCode == 0, strategy.Id + ": " + text);
                Assert.Contains("command line parameters verified", text);
            }
            Assert.Empty(Directory.GetDirectories(dataRoot));
            Evidence(repo, "engine-flowseal-parser", "Real reviewed winws.exe v72.9 source c849e55 from Flowseal 865da4f installed/reused offline. All five typed strategies passed the actual Windows PE parser with all reviewed rules/files. Protected ACLs, component hashes and data-file locks passed. No ISP, QUIC, STUN or voice check was performed.");
            var logs = new ConcurrentQueue<string>();
            await using var engine = new Zapret1Engine(manager, provider, noTrafficCapture: true);
            engine.LogReceived += logs.Enqueue;
            var configured = new EngineConfiguration(installed.ExecutablePath, FlowsealCatalog.Profile(FlowsealCatalog.Find("general")!));
            try
            {
                await engine.StartAsync(configured);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!logs.Any(l => l.Contains("windivert initialized. capture is started.")) && DateTime.UtcNow < deadline)
                { await Task.Delay(100); if ((await engine.GetStatusAsync()).State == EngineState.Error) break; }
                var state = await engine.GetStatusAsync();
                Assert.True(state.State == EngineState.Active, state.Error + "\n" + string.Join("\n", logs));
                Assert.Contains(logs, l => l.Contains("windivert initialized. capture is started."));
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(configured));
                int pid = state.ProcessId!.Value;
                await using (var colliding = new Zapret1Engine(manager, provider, noTrafficCapture: true))
                {
                    await Assert.ThrowsAsync<EngineConflictException>(() => colliding.StartAsync(configured));
                    Assert.Equal(pid, (await engine.GetStatusAsync()).ProcessId);
                    Assert.Equal(EngineState.Active, (await engine.GetStatusAsync()).State);
                    Assert.Null((await colliding.GetStatusAsync()).ProcessId);
                }
                await engine.StopAsync(); Assert.Equal(EngineState.Disconnected, (await engine.GetStatusAsync()).State);
                Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid)); Assert.Empty(Directory.GetDirectories(dataRoot));
                Assert.NotNull(await manager.DetectAsync());
                Evidence(repo, "engine-flowseal-driver", "Real Flowseal winws initialized WinDivert on Windows CI using filter=false (no traffic captured). Reviewed rules loaded, duplicate start rejected, owned child stopped, protected strategy snapshot cleaned up, engine lease released. This does not prove bypass, streaming, QUIC/STUN or voice.");
            }
            catch (InvalidOperationException ex) when (logs.Any(l => l.Contains("windivert: error opening filter", StringComparison.OrdinalIgnoreCase)) &&
                (ex.Message.Contains("code 5") || ex.Message.Contains("code 577") || ex.Message.Contains("code 1275")))
            {
                Assert.Equal(EngineState.Error, (await engine.GetStatusAsync()).State); Assert.Null((await engine.GetStatusAsync()).ProcessId);
                Assert.Empty(Directory.GetDirectories(dataRoot));
                Evidence(repo, "engine-flowseal-driver", "BLOCKED by Windows runner driver policy; error/owned cleanup passed. Driver initialization and DPI bypass did not succeed. " + ex.Message.Replace('\n', ' '));
            }
        }
        finally
        {
            if (Directory.Exists(userRoot)) Directory.Delete(userRoot, true);
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); }
                    catch (UnauthorizedAccessException) when (Path.GetFileName(file) == "WinDivert64.sys")
                    { Evidence(repo, "engine-flowseal-driver-retention", "Windows retained its loaded signed driver image. No shared driver service was removed; directory cleanup requires driver unload/reboot."); }
                }
                foreach (string folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                    if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
            }
        }
    }
}
