using System.Text.Json;
using Northpass.Models;
using Northpass.Services;

namespace Northpass.Tests;

public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "northpass-tests-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public sealed class ProfileTests
{
    public static StrategyProfile Profile(string id = "test") => new() { Id = id, Name = "Test", Arguments = ["--wf-tcp-out=443"] };
    [Theory]
    [InlineData("../outside")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("a b")]
    public void IdCannotEscapeProfileDirectory(string id) => Assert.False(ProfileValidation.Validate(Profile(id)).IsValid);
    [Fact]
    public void EmptyProfileIsDraftButCannotConnect()
    {
        var profile = Profile(); profile.Arguments.Clear();
        Assert.True(ProfileValidation.Validate(profile, false).IsValid);
        Assert.False(ProfileValidation.Validate(profile).IsValid);
    }
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Id\":\"test\",\"Name\":\"T\",\"Arguments\":null}")]
    [InlineData("{\"Id\":\"test\",\"Name\":\"T\",\"Arguments\":[null]}")]
    [InlineData("{\"Id\":\"test\",\"Name\":\"T\",\"Arguments\":[\"a\\nb\"]}")]
    public void MalformedProfilesAreRejected(string json) => Assert.ThrowsAny<Exception>(() => ProfileValidation.Parse(json));
    [Fact]
    public void RoundTripAndDuplicateImportPreserveOriginal()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ProfileStore(System.IO.Path.Combine(temporary.Path, "profiles"));
        var saved = store.Save(Profile());
        Assert.Single(store.Load().Profiles);
        string exported = System.IO.Path.Combine(temporary.Path, "export.json");
        store.Export(saved, exported);
        Assert.DoesNotContain("SourcePath", File.ReadAllText(exported));
        Assert.Throws<IOException>(() => store.Import(exported));
        Assert.Equal("Test", store.Load().Profiles.Single().Name);
        var edited = ProfileValidation.Copy(saved); edited.Name = "Edited";
        store.Update(saved, edited);
        Assert.Equal("Edited", store.Load().Profiles.Single().Name);
    }
    [Fact]
    public void ImportDoesNotAcceptExecutableOrSourcePathOverrides()
    {
        var profile = ProfileValidation.Parse("{\"Id\":\"safe\",\"Name\":\"Safe\",\"ExecutablePath\":\"cmd.exe\",\"SourcePath\":\"outside.json\",\"Arguments\":[]}");
        Assert.Equal("", profile.SourcePath);
        Assert.Equal("zapret1", profile.Engine);
    }
    [Fact]
    public void DuplicateIdInDifferentlyNamedFileCannotBeImported()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ProfileStore(temporary.Path);
        File.WriteAllText(System.IO.Path.Combine(temporary.Path, "bundled-template.json"), JsonSerializer.Serialize(Profile(), ProfileValidation.JsonOptions));
        Assert.Throws<IOException>(() => store.Save(Profile("TEST")));
        Assert.Single(store.Load().Profiles);
    }
    [Fact]
    public void InvalidFileDoesNotHideHealthyProfiles()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ProfileStore(temporary.Path);
        store.Save(Profile());
        File.WriteAllText(System.IO.Path.Combine(temporary.Path, "broken.json"), "{");
        var result = store.Load();
        Assert.Single(result.Profiles); Assert.Single(result.Errors);
    }
    [Fact]
    public void UpdateCannotWriteOutsideStore()
    {
        using var temporary = new TemporaryDirectory();
        var store = new ProfileStore(System.IO.Path.Combine(temporary.Path, "profiles"));
        var original = Profile(); original.SourcePath = System.IO.Path.Combine(temporary.Path, "external.json");
        Assert.Throws<IOException>(() => store.Update(original, Profile()));
    }
    [Fact]
    public void SettingsPersistAndCorruptionIsNotOverwritten()
    {
        using var temporary = new TemporaryDirectory();
        var store = new SettingsStore(temporary.Path);
        store.Save(new() { SelectedProfileId = "test", Language = "az", EnginePath = "C:\\engine\\winws.exe", AutoRecover = true });
        var loaded = store.Load();
        Assert.Equal("test", loaded.SelectedProfileId); Assert.Equal("az", loaded.Language); Assert.True(loaded.AutoRecover);
        string path = System.IO.Path.Combine(temporary.Path, "settings.json");
        File.WriteAllText(path, "broken");
        Assert.Throws<JsonException>(store.Load);
        Assert.Equal("broken", File.ReadAllText(path));
    }
}
