using Northpass.Models;
using Northpass.Services;
using Northpass.Services.Installation;

namespace Northpass.Tests;

public sealed class DataListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "northpass-data-" + Guid.NewGuid().ToString("N"));
    private DataListStore Store => new(Path.Combine(_root, "lists"));
    private string Input(string data, string extension = "txt")
    { Directory.CreateDirectory(_root); string path = Path.Combine(_root, "input." + extension); File.WriteAllText(path, data); return path; }
    [Theory]
    [InlineData("bat")][InlineData("exe")][InlineData("dll")][InlineData("lua")][InlineData("ps1")][InlineData("json")]
    public void ImportsRejectCodeEvenIfItContainsAValidDomain(string extension)
        => Assert.Throws<InvalidDataException>(() => Store.Import(Input("youtube.com", extension), DataListKind.Hosts));
    [Theory]
    [InlineData("https://youtube.com")][InlineData("--hostlist=evil")][InlineData("google.com & calc")]
    [InlineData("^^discord.com")][InlineData("a..com")][InlineData("*.example.com")][InlineData("# empty\n")]
    public void HostlistsRejectCommandsAndInvalidDomains(string data)
        => Assert.Throws<InvalidDataException>(() => DataListStore.Normalize(data, DataListKind.Hosts));
    [Theory]
    [InlineData("999.1.1.1")][InlineData("1.2.3.4/33")][InlineData("::1/129")][InlineData("127.1")]
    [InlineData("0x7f.0.0.1")][InlineData("fe80::1%12")][InlineData("1.2.3.4 & calc")]
    public void IpSetsRejectNonAddressData(string data)
        => Assert.Throws<InvalidDataException>(() => DataListStore.Normalize(data, DataListKind.IpSet));
    [Fact]
    public void ImportsNormalizeDeduplicateAndRevalidateLaterEdits()
    {
        var store = Store; var entry = store.Import(Input("# note\nYouTube.com\nyoutube.com\n^Discord.com\n"), DataListKind.Hosts);
        Assert.Equal("youtube.com\n^discord.com\n", store.Read(entry.Id, DataListKind.Hosts));
        Assert.Throws<InvalidDataException>(() => store.Read(entry.Id, DataListKind.IpSet));
        File.WriteAllText(Path.Combine(store.DirectoryPath, entry.Id + ".txt"), "--lua-init=evil.lua");
        Assert.Throws<InvalidDataException>(() => store.Read(entry.Id, DataListKind.Hosts));
        Assert.Throws<InvalidDataException>(() => store.Read("../../outside", DataListKind.Hosts));
    }
    [Fact]
    public void InvalidUtf8OversizeAndSymlinksFailClosed()
    {
        string path = Input("valid.com"); File.WriteAllBytes(path, [0xff, 0xfe, 0xff]);
        Assert.Throws<System.Text.DecoderFallbackException>(() => Store.Import(path, DataListKind.Hosts));
        File.WriteAllBytes(path, new byte[DataListStore.MaximumBytes + 1]);
        Assert.Throws<InvalidDataException>(() => Store.Import(path, DataListKind.Hosts));
        string link = Path.Combine(_root, "link.txt"); File.CreateSymbolicLink(link, path);
        Assert.Throws<IOException>(() => Store.Import(link, DataListKind.Hosts));
    }
    [Fact]
    public async Task ProtectedSessionDataIsSnapshotAndNeverChangesBundledLists()
    {
        var store = Store; var hosts = store.Import(Input("youtube.com"), DataListKind.Hosts);
        var ips = store.Import(Input("1.2.3.4/24\n2001:db8::/32"), DataListKind.IpSet);
        var profile = new StrategyProfile { ListBindings = new() { ["general"] = hosts.Id, ["all-ips"] = ips.Id } };
        var provider = new ProtectedEngineDataProvider(store, Path.Combine(_root, "protected"), new FixtureSecurity());
        Assert.True((await provider.ValidateAsync(profile)).IsValid);
        var lease = await provider.PrepareAsync(profile); string directory = lease.DirectoryPath;
        Assert.Equal("youtube.com\n", File.ReadAllText(Path.Combine(directory, "list-general-user.txt")));
        Assert.Equal("domain.example.abc\n", File.ReadAllText(Path.Combine(directory, "list-exclude-user.txt")));
        Assert.Equal("203.0.113.113/32\n", File.ReadAllText(Path.Combine(directory, "ipset-exclude-user.txt")));
        Assert.Equal("1.2.3.4/24\n2001:db8::/32\n", File.ReadAllText(lease.AllIpsPath!));
        File.WriteAllText(Path.Combine(store.DirectoryPath, hosts.Id + ".txt"), "discord.com");
        Assert.Equal("youtube.com\n", File.ReadAllText(Path.Combine(directory, "list-general-user.txt")));
        await lease.DisposeAsync(); await lease.DisposeAsync(); Assert.False(Directory.Exists(directory));
        profile.ListBindings["all-ips"] = hosts.Id; Assert.False((await provider.ValidateAsync(profile)).IsValid);
        profile.ListBindings["unknown"] = hosts.Id; Assert.False((await provider.ValidateAsync(profile)).IsValid);
    }
    private sealed class FixtureSecurity : IInstallationSecurity
    {
        public void PrepareRoot(string path) { SafeArchive.NoLinks(path); Directory.CreateDirectory(path); }
        public void ProtectDirectory(string path) => SafeArchive.NoLinks(path);
        public void ValidateDirectory(string path) => SafeArchive.NoLinks(path);
        public void ProtectFile(string path) => SafeArchive.NoLinks(path);
        public void ValidateFile(string path) => SafeArchive.NoLinks(path);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
