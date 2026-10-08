using Northpass.Engine.Zapret2;
using Northpass.Models;

namespace Northpass.Tests;

public sealed class Zapret2ValidationTests
{
    private static EngineConfiguration Bundle(string directory)
    {
        foreach (string name in Zapret2ConfigurationValidator.RequiredFiles.Append("winws2.exe")) File.WriteAllText(Path.Combine(directory, name), "fixture, not an engine binary");
        var profile = ProfileTests.Profile(); profile.SourcePath = Path.Combine(directory, "profile.json");
        return new(Path.Combine(directory, "winws2.exe"), profile);
    }
    [Fact]
    public void RequiredDependenciesAreReported()
    {
        using var directory = new TemporaryDirectory(); var config = Bundle(directory.Path);
        File.Delete(Path.Combine(directory.Path, "WinDivert64.sys"));
        Assert.Contains(Zapret2ConfigurationValidator.Validate(config).Issues, i => i.Code == "engine.dependency" && i.Message.Contains("WinDivert64.sys"));
    }
    [Fact]
    public void FileLuaAndHostlistPathsAreValidatedAfterExpansion()
    {
        using var directory = new TemporaryDirectory(); var config = Bundle(directory.Path);
        config.Profile.Arguments.Add("--lua-init=@{ENGINE_DIR}/strategy.lua");
        config.Profile.Arguments.Add("--hostlist={PROFILE_DIR}/hosts.txt");
        config.Profile.Arguments.Add("--blob=fake:+4@{ENGINE_DIR}/payload.bin");
        Assert.Equal(3, Zapret2ConfigurationValidator.Validate(config).Issues.Count(i => i.Code == "argument.file"));
        File.WriteAllText(Path.Combine(directory.Path, "strategy.lua"), "-- fixture");
        File.WriteAllText(Path.Combine(directory.Path, "hosts.txt.gz"), "gzip fallback fixture");
        File.WriteAllText(Path.Combine(directory.Path, "payload.bin"), "fixture");
        Assert.True(Zapret2ConfigurationValidator.Validate(config).IsValid);
    }
    [Theory]
    [InlineData("--lua-init=os.execute('cmd')", "argument.lua")]
    [InlineData("--daemon", "argument.lifecycle")]
    [InlineData("--daemo", "argument.unknown")]
    [InlineData("--lua-ini=@trusted.lua", "argument.unknown")]
    [InlineData("--wf-tcp-out", "argument.value")]
    [InlineData("--intercept=00", "argument.lifecycle")]
    [InlineData("--intercept=0", "argument.lifecycle")]
    [InlineData("--wf-dup-check=0", "argument.lifecycle")]
    [InlineData("--dpi-desync=fake", "argument.zapret1")]
    [InlineData("cmd.exe /c echo bad", "argument.option")]
    [InlineData("--hostlist={UNKNOWN}/hosts.txt", "argument.placeholder")]
    public void UnsafeOrIncompatibleArgumentsAreRejected(string argument, string code)
    {
        using var directory = new TemporaryDirectory(); var config = Bundle(directory.Path);
        config.Profile.Arguments.Add(argument);
        Assert.Contains(Zapret2ConfigurationValidator.Validate(config).Issues, i => i.Code == code);
    }
    [Fact]
    public void NativeOrArbitraryExecutableCannotUseZapretAdapter()
    {
        using var directory = new TemporaryDirectory(); var config = Bundle(directory.Path);
        config.Profile.Engine = "native";
        var issues = Zapret2ConfigurationValidator.Validate(config with { ExecutablePath = Path.Combine(directory.Path, "cmd.exe") }).Issues;
        Assert.Contains(issues, i => i.Code == "engine.id"); Assert.Contains(issues, i => i.Code == "engine.name");
    }
    [Fact]
    public async Task ActualZapretStartIsRejectedOnNonWindows()
    {
        if (OperatingSystem.IsWindows()) return; // OS-specific guard, no claim of Windows engine testing.
        await using var engine = new Zapret2Engine();
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => engine.StartAsync(new("missing", ProfileTests.Profile())));
    }
}
