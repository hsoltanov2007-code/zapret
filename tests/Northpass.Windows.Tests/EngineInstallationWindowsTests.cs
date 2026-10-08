using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Collections.Concurrent;
using Northpass.Desktop;
using Northpass.Engine.Zapret2;
using Northpass.Models;
using Northpass.Services.Installation;

namespace Northpass.Windows.Tests;

public sealed class EngineInstallationWindowsTests
{
    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Northpass.sln"))) return folder.FullName;
        throw new DirectoryNotFoundException("Run Windows integration tests from the repository checkout.");
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; throw new InvalidOperationException("Offline installation attempted a network request."); }
    }
    private static void Evidence(string repo, string name, string value)
    {
        Directory.CreateDirectory(Path.Combine(repo, "TestResults"));
        File.WriteAllText(Path.Combine(repo, "TestResults", name + ".txt"), value);
    }
    [Fact]
    public async Task RealPinnedEngineInstallsOfflineWithProtectedAclsAndValidatesVersionAndStrategy()
    {
        string repo = RepositoryRoot();
        string payload = Path.Combine(repo, "dist/Northpass/engine-payload/zapret2-offline.zip");
        Assert.True(File.Exists(payload), "Run python scripts/prepare-engine.py before Windows integration tests.");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-engine-test-" + Guid.NewGuid().ToString("N"));
        using var manifestFile = Zapret2Catalog.OpenTrustedManifest();
        var manifest = EngineManifest.Parse(manifestFile);
        using var handler = new NoNetwork(); using var http = new HttpClient(handler);
        var security = new WindowsInstallationSecurity();
        var manager = new EngineInstallationManager(root, http, security, manifest, offlinePayload: payload,
            probe: Zapret2Engine.VerifyInstalledVersionAsync);
        try
        {
            Assert.Null(await manager.DetectAsync());
            var installed = await manager.EnsureInstalledAsync();
            Assert.Equal(installed, await manager.DetectAsync()); Assert.Equal(installed, await manager.EnsureInstalledAsync());
            Assert.Equal(0, handler.Requests);
            security.ValidateDirectory(root);
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(installed.ExecutablePath)!, "*", SearchOption.AllDirectories)) security.ValidateFile(file);
            // Real PE executable and runtime, no driver capture: actual pinned strategy parser validation.
            var profile = ProfileValidation.Parse(await File.ReadAllTextAsync(Path.Combine(repo, "profiles/zapret2-reviewed-example.json")));
            profile.SourcePath = Path.Combine(repo, "profiles/zapret2-reviewed-example.json");
            Assert.True(Zapret2ConfigurationValidator.Validate(new(installed.ExecutablePath, profile)).IsValid);
            var probe = new ProcessStartInfo(installed.ExecutablePath) { WorkingDirectory = Path.GetDirectoryName(installed.ExecutablePath)!,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string argument in profile.Arguments) probe.ArgumentList.Add(Zapret2ConfigurationValidator.Expand(argument, probe.WorkingDirectory, Path.Combine(repo, "profiles")));
            probe.ArgumentList.Add("--dry-run");
            using (var process = Process.Start(probe)!)
            {
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(timeout.Token); }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                string text = await output + await error;
                Assert.True(process.ExitCode == 0, text); Assert.Contains("command line parameters verified", text);
            }
            await using (var lease = await manager.AcquireLaunchLeaseAsync(installed.ExecutablePath))
            {
                Assert.Throws<IOException>(() => File.Open(installed.ExecutablePath, FileMode.Open, FileAccess.Write, FileShare.Read));
                await Assert.ThrowsAsync<IOException>(() => manager.UpdateAsync());
            }
            // An ACL made user-writable must fail even if all component bytes still match.
            var runtime = new FileInfo(Path.Combine(Path.GetDirectoryName(installed.ExecutablePath)!, "cygwin1.dll"));
            var unsafeAcl = runtime.GetAccessControl();
            unsafeAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Allow));
            runtime.SetAccessControl(unsafeAcl);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.DetectAsync());
            security.ProtectFile(runtime.FullName);
            Assert.Equal(installed, await manager.DetectAsync());
            File.Copy(runtime.FullName, Path.Combine(runtime.DirectoryName!, "ole32.dll"));
            security.ProtectFile(Path.Combine(runtime.DirectoryName!, "ole32.dll"));
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.DetectAsync());
            Evidence(repo, "engine-installation", "Real pinned x64 PE version and strategy dry-run passed; offline reuse, protected ACL rejection and launch file locks passed. No DPI bypass was tested.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task DriverLifecycleWithFalseFilterReportsInitializationOrExplicitPolicyFailure()
    {
        string repo = RepositoryRoot();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Northpass-driver-test-" + Guid.NewGuid().ToString("N"));
        using var stream = Zapret2Catalog.OpenTrustedManifest();
        using var handler = new NoNetwork(); using var http = new HttpClient(handler);
        var manager = new EngineInstallationManager(root, http, new WindowsInstallationSecurity(), EngineManifest.Parse(stream),
            offlinePayload: Path.Combine(repo, "dist/Northpass/engine-payload/zapret2-offline.zip"), probe: Zapret2Engine.VerifyInstalledVersionAsync);
        try
        {
            var installed = await manager.EnsureInstalledAsync();
            var profile = new StrategyProfile { Id = "ci-no-traffic", Name = "CI no traffic", Engine = "zapret2",
                SourcePath = Path.Combine(repo, "profiles/ci-no-traffic.json"), Arguments = ["--wf-raw=false",
                    "--lua-init=@{ENGINE_DIR}/lua/zapret-lib.lua", "--lua-init=@{ENGINE_DIR}/lua/zapret-antidpi.lua",
                    "--filter-tcp=443", "--payload=tls_client_hello", "--lua-desync=multisplit:pos=1,midsld"] };
            var logs = new ConcurrentQueue<string>();
            await using var engine = new Zapret2Engine(manager); engine.LogReceived += logs.Enqueue;
            try
            {
                await engine.StartAsync(new(installed.ExecutablePath, profile));
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!logs.Any(l => l.Contains("windivert initialized. capture is started.")) && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                    if ((await engine.GetStatusAsync()).State == EngineState.Error) break;
                }
                var state = await engine.GetStatusAsync();
                Assert.True(state.State == EngineState.Active, state.Error + "\n" + string.Join("\n", logs));
                Assert.Contains(logs, l => l.Contains("windivert initialized. capture is started."));
                await Task.Delay(500); Assert.Equal(EngineState.Active, (await engine.GetStatusAsync()).State);
                int pid = state.ProcessId!.Value;
                await engine.StopAsync(); Assert.Equal(EngineState.Disconnected, (await engine.GetStatusAsync()).State);
                Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
                Assert.NotNull(await manager.DetectAsync());
                Evidence(repo, "engine-driver", "WinDivert initialized on this Windows CI runner with filter=false (no traffic captured); reviewed Lua loaded, owned process stopped and installation lease released. No ISP/DPI bypass test was performed.");
            }
            catch (InvalidOperationException ex) when (logs.Any(l => l.Contains("windivert: error opening filter", StringComparison.OrdinalIgnoreCase)) &&
                (ex.Message.Contains("code 5") || ex.Message.Contains("code 577") || ex.Message.Contains("code 1275")))
            {
                Assert.Equal(EngineState.Error, (await engine.GetStatusAsync()).State);
                Assert.Null((await engine.GetStatusAsync()).ProcessId);
                Evidence(repo, "engine-driver", "DRIVER INITIALIZATION BLOCKED by Windows runner security policy. Error/cleanup were verified; driver initialization and DPI bypass were NOT successful. " + ex.Message.Replace('\n', ' '));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
