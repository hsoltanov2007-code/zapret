using System.Diagnostics;
using Northpass.Models;
namespace Northpass.Services;

public sealed class DiagnosticsService(HttpClient client)
{
    public async Task<ReachabilityResult> TestAsync(string address, CancellationToken token = default)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0)
            throw new ArgumentException("Enter an HTTPS URL without credentials.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return new(uri, response.IsSuccessStatusCode, (int)response.StatusCode, watch.Elapsed,
                $"HTTP {(int)response.StatusCode}. This checks this URL only; it does not establish a DPI bypass.");
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !token.IsCancellationRequested)
        { return new(uri, false, null, watch.Elapsed, ex.Message); }
    }
}
