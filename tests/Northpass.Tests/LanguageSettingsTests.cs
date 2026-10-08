using Northpass.Services;

namespace Northpass.Tests;

public sealed class LanguageSettingsTests
{
    [Fact]
    public void FirstLaunchUsesRussianWithoutCreatingSettings()
    {
        using var folder = new TemporaryDirectory();
        var store = new SettingsStore(folder.Path);
        Assert.Equal("ru", store.Load().Language);
        Assert.False(File.Exists(Path.Combine(folder.Path, "settings.json")));
    }
    [Theory]
    [InlineData("{}", "ru")]
    [InlineData("{\"Language\":\"en\",\"ShowAdvancedTools\":true}", "en")]
    [InlineData("{\"Language\":\"az\"}", "az")]
    [InlineData("{\"Language\":\"ru\"}", "ru")]
    [InlineData("{\"Language\":\"unknown\"}", "ru")]
    [InlineData("{\"Language\":null}", "ru")]
    public void SavedLanguagesSurviveAndUnsupportedValuesFallBackWithoutRewritingTheFile(string json, string expected)
    {
        using var folder = new TemporaryDirectory();
        string path = Path.Combine(folder.Path, "settings.json"); File.WriteAllText(path, json);
        Assert.Equal(expected, new SettingsStore(folder.Path).Load().Language);
        Assert.Equal(json, File.ReadAllText(path));
    }
}
