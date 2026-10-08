using System.Net;
using Northpass.Services;

namespace Northpass.Tests;

public sealed class DiagnosticsTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(respond(request)); }
    }
    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Redirect, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task ReachabilityReportsHttpOutcomeWithoutProtectionClaim(HttpStatusCode code, bool reachable)
    {
        using var handler = new Handler(_ => new(code)); using var http = new HttpClient(handler);
        var result = await new DiagnosticsService(http).TestAsync("https://example.com/");
        Assert.Equal(reachable, result.Reachable); Assert.Equal((int)code, result.HttpStatus);
        Assert.Contains("does not establish a DPI bypass", result.Detail); Assert.Equal(1, handler.Requests);
    }
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("file:///local")]
    [InlineData("https://user:password@example.com")]
    public async Task InvalidUrlsMakeNoNetworkRequest(string url)
    {
        using var handler = new Handler(_ => new(HttpStatusCode.OK)); using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new DiagnosticsService(http).TestAsync(url));
        Assert.Equal(0, handler.Requests);
    }
    [Fact]
    public async Task NetworkErrorsAreReportedAsFailures()
    {
        using var handler = new Handler(_ => throw new HttpRequestException("unreachable")); using var http = new HttpClient(handler);
        var result = await new DiagnosticsService(http).TestAsync("https://example.com");
        Assert.False(result.Reachable); Assert.Null(result.HttpStatus);
    }
    [Theory]
    [InlineData("v0.3.0", "Update available")]
    [InlineData("v0.2.0", "Latest published release")]
    public async Task UpdateCheckUsesMetadataOnly(string tag, string expected)
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"tag_name\":\"" + tag + "\"}") };
        });
        using var http = new HttpClient(handler);
        Assert.Contains(expected, await new UpdateChecker(http).CheckAsync()); Assert.Equal(1, handler.Requests);
    }
    [Fact]
    public async Task NoReleaseIsNotAnUpdateFailure()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.NotFound)); using var http = new HttpClient(handler);
        Assert.Contains("No published release", await new UpdateChecker(http).CheckAsync());
    }
}
