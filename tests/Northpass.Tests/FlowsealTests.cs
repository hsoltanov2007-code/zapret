using Northpass.Engine;
using Northpass.Engine.Zapret1;
using Northpass.Models;
using Northpass.Services.Installation;

namespace Northpass.Tests;

public sealed class FlowsealTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "northpass-flowseal-" + Guid.NewGuid().ToString("N"));
    private string Prepare(FlowsealStrategy strategy)
    {
        foreach (string asset in FlowsealCatalog.Assets)
        { string path = Path.Combine(_root, asset); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture, not a Windows engine"); }
        return Path.Combine(_root, "bin/winws.exe");
    }
    [Fact]
    public void ManifestPinsExactReviewedBundleAndIncludesOnlyRequiredDataAndBinaries()
    {
        using var input = FlowsealCatalog.OpenTrustedManifest(); var manifest = EngineManifest.Parse(input);
        Assert.Equal(FlowsealCatalog.BundleCommit, manifest.Revision); Assert.Equal(FlowsealCatalog.SourceCommit, manifest.SourceRevision);
        Assert.Equal("72.9", manifest.Version); Assert.Equal("bin/winws.exe", manifest.Executable);
        Assert.Contains(FlowsealCatalog.BundleCommit, manifest.ArchiveUrl);
        Assert.Equal(18, manifest.Components.Count); Assert.Equal(3310911, manifest.OfflineSize);
        Assert.Contains(manifest.Components, c => c.Path == "bin/WinDivert64.sys");
        Assert.DoesNotContain(manifest.Components, c => c.Path.EndsWith(".bat") || c.Path.EndsWith(".lua") || c.Path.Contains("service"));
    }
    [Theory]
    [InlineData("general")][InlineData("alt")][InlineData("alt2")][InlineData("simple-fake")][InlineData("fake-tls-auto")]
    public void EveryReviewedStrategyPreservesRuleBoundariesRepeatedOptionsAndVoice(string id)
    {
        var strategy = FlowsealCatalog.Find(id)!; Assert.NotNull(strategy);
        var profile = FlowsealCatalog.Profile(strategy); string exe = Prepare(strategy);
        Assert.True(Zapret1ConfigurationValidator.Validate(new(exe, profile)).IsValid);
        Assert.Equal(9, strategy.Rules.Count); Assert.Equal(8, profile.Arguments.Count(a => a == "--new"));
        Assert.Contains("--filter-udp=19294-19344,50000-50100", profile.Arguments);
        Assert.Contains("--filter-l7=discord,stun", profile.Arguments);
        Assert.Contains(profile.Arguments, a => a.Contains("ACTIVE_DISCORD_UDP.bin"));
        Assert.Contains(profile.Arguments, a => a.Contains("list-google.txt"));
        Assert.True(profile.Arguments.Count(a => a.StartsWith("--hostlist=")) >= 4);
        foreach (var rule in strategy.Rules)
            foreach (var option in rule.Where(o => o.Kind is StrategyValueKind.BinAsset or StrategyValueKind.BundleList))
                Assert.Contains((option.Kind == StrategyValueKind.BinAsset ? "bin/" : "lists/") + option.Value, FlowsealCatalog.Assets);
        var rendered = FlowsealCatalog.Compile(strategy, Path.Combine(_root, "bin"), Path.Combine(_root, "lists"), _root, "1024-65535", "3478,50000-50100");
        Assert.DoesNotContain(rendered, a => a.Contains("{GAME_") || a.Contains("%"));
        Assert.Contains(rendered, a => a.Contains("1024-65535"));
        using var lease = new FixtureLease(_root);
        var info = Zapret1Engine.CreateStartInfo(new(exe, profile), lease);
        Assert.False(info.UseShellExecute); Assert.Empty(info.Arguments); Assert.Equal(Path.GetFullPath(exe), info.FileName);
        Assert.Contains(info.ArgumentList, a => a.StartsWith("--wf-tcp=80,443,", StringComparison.Ordinal));
        Assert.Contains(info.ArgumentList, a => a.StartsWith("--wf-udp=443,", StringComparison.Ordinal));
        Assert.DoesNotContain(info.ArgumentList, a => a.StartsWith("--wf-raw", StringComparison.Ordinal));
        Assert.DoesNotContain(info.ArgumentList, a => a.StartsWith("--debug", StringComparison.Ordinal));
        Assert.DoesNotContain("CYGWIN", info.Environment.Keys); Assert.DoesNotContain("LUA_INIT", info.Environment.Keys);
        var noCapture = Zapret1Engine.CreateStartInfo(new(exe, profile), lease, true);
        Assert.Equal("--wf-raw=false", noCapture.ArgumentList[0]); Assert.DoesNotContain(noCapture.ArgumentList, a => a.StartsWith("--wf-tcp="));
    }
    [Fact]
    public void RepeatedFakesAndLiteralExclamationSurviveWithoutShellEscaping()
    {
        var alt = FlowsealCatalog.Templates(FlowsealCatalog.Find("alt")!);
        Assert.True(alt.Count(a => a.StartsWith("--dpi-desync-fake-tls=")) > 1);
        var tls = FlowsealCatalog.Templates(FlowsealCatalog.Find("fake-tls-auto")!);
        Assert.Contains(tls, a => a.Contains("!")); Assert.DoesNotContain(tls, a => a.Contains("^!"));
        Assert.Throws<NotSupportedException>(() => ((IList<StrategyOption>)FlowsealCatalog.Find("general")!.Rules[0])[0] = new("--bad", StrategyValueKind.Literal, "1"));
    }
    [Theory]
    [InlineData("--lua-init=@evil.lua")][InlineData("--wf-raw=true")][InlineData("--daemon")][InlineData("--hostlist=C:/untrusted.txt")]
    public void ArbitraryImportedArgumentsCannotReachWinws(string argument)
    {
        var strategy = FlowsealCatalog.Find("general")!; var profile = FlowsealCatalog.Profile(strategy);
        string exe = Prepare(strategy); profile.Arguments.Add(argument);
        Assert.False(Zapret1ConfigurationValidator.Validate(new(exe, profile)).IsValid);
        profile.Arguments.RemoveAt(profile.Arguments.Count - 1); profile.Arguments.Reverse();
        Assert.False(Zapret1ConfigurationValidator.Validate(new(exe, profile)).IsValid);
    }
    [Fact]
    public void MissingFakeAndIncorrectEngineFailValidation()
    {
        var strategy = FlowsealCatalog.Find("general")!; var profile = FlowsealCatalog.Profile(strategy); string exe = Prepare(strategy);
        File.Delete(Path.Combine(_root, "bin/ACTIVE_DISCORD_UDP.bin"));
        Assert.False(Zapret1ConfigurationValidator.Validate(new(exe, profile)).IsValid);
        profile.Engine = "zapret2"; Assert.False(Zapret1ConfigurationValidator.Validate(new(exe, profile)).IsValid);
    }
    [Theory]
    [InlineData("12", true)][InlineData("443,1024-65535", true)][InlineData("0", false)][InlineData("65536", false)]
    [InlineData("2-1", false)][InlineData("%GameFilterTCP%", false)][InlineData("443 & calc", false)][InlineData("1,,2", false)]
    public void PortInputsNeverExpandEnvironmentOrCommands(string ports, bool valid) => Assert.Equal(valid, Zapret1Options.ValidPorts(ports));
    [Fact]
    public void OldProfileDefaultsAndDeepCopyKeepTypedInputsIndependent()
    {
        var old = ProfileValidation.Parse("{\"Id\":\"old\",\"Name\":\"old\",\"Engine\":\"zapret2\",\"Arguments\":[\"--filter-tcp=443\"]}");
        Assert.Equal("12", old.GameTcpPorts); Assert.Empty(old.ListBindings);
        old.ListBindings.Add("general", "before"); var copy = ProfileValidation.Copy(old);
        old.ListBindings["general"] = "after"; old.GameTcpPorts = "443";
        Assert.Equal("before", copy.ListBindings["general"]); Assert.Equal("12", copy.GameTcpPorts);
    }
    private sealed class FixtureLease(string path) : IEngineDataLease, IDisposable
    {
        public string DirectoryPath => path; public string? AllIpsPath => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; public void Dispose() { }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
