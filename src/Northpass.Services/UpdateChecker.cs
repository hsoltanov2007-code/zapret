using System.Text.Json;
namespace Northpass.Services;

public enum AppUpdateState { UpdateAvailable, UpToDate, NoPublishedUpdate }
public sealed record AppUpdateResult(AppUpdateState State, string Detail);
// Opt-in metadata only. No asset download/execution or product-facing upstream text.
public sealed class UpdateChecker(HttpClient client)
{
    public static Version CurrentVersion { get; } = new(0, 6, 0);
    public async Task<AppUpdateResult> CheckAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/hsoltanov2007-code/zapret/releases/latest");
        request.Headers.UserAgent.ParseAdd("Northpass/0.6");
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new(AppUpdateState.NoPublishedUpdate, "No published application update.");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        string tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) throw new InvalidDataException("Application update metadata does not contain a valid version.");
        return new(latest > CurrentVersion ? AppUpdateState.UpdateAvailable : AppUpdateState.UpToDate, $"Published version {latest}; current {CurrentVersion}. No binaries were downloaded.");
    }
}
