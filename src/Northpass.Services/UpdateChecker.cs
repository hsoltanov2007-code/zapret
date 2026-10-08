using System.Text.Json;
namespace Northpass.Services;

// Opt-in metadata check only. Never downloads or executes release assets.
public sealed class UpdateChecker(HttpClient client)
{
    public async Task<string> CheckAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/hsoltanov2007-code/zapret/releases/latest");
        request.Headers.UserAgent.ParseAdd("Northpass/0.5");
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return "No published release is available yet.";
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        string tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
        return Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && latest > new Version(0, 4, 0)
            ? $"Update available: {tag}. Review the release on GitHub before installing."
            : $"Latest published release: {tag}. Current version: 0.5.0.";
    }
}
