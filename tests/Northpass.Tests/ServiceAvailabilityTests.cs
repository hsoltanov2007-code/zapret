using Northpass.Services;
using System.Net;

namespace Northpass.Tests;

public sealed class ServiceAvailabilityTests
{
    private static ServiceProbeResult Result(ProbeState dns, ProbeState tcp, ProbeState tls, ProbeState http)
    {
        var unknown = new ProbeObservation(ProbeState.Unknown, "not tested");
        return new("youtube", "YouTube", "www.youtube.com", [], null, new(dns, "fixture"), new(tcp, "fixture"), new(tls, "fixture"), new(http, "fixture"),
            unknown, unknown, unknown, unknown, unknown, unknown);
    }
    [Theory]
    [InlineData(ProbeState.Unknown, ProbeState.Unknown, ProbeState.Unknown, ProbeState.Unknown, ServiceAvailability.Unknown)]
    [InlineData(ProbeState.Passed, ProbeState.Passed, ProbeState.Passed, ProbeState.Passed, ServiceAvailability.Available)]
    [InlineData(ProbeState.Failed, ProbeState.Unknown, ProbeState.Unknown, ProbeState.Unknown, ServiceAvailability.Unavailable)]
    [InlineData(ProbeState.Passed, ProbeState.Failed, ProbeState.Unknown, ProbeState.Unknown, ServiceAvailability.Unavailable)]
    [InlineData(ProbeState.Passed, ProbeState.Passed, ProbeState.Failed, ProbeState.Unknown, ServiceAvailability.Limited)]
    [InlineData(ProbeState.Passed, ProbeState.Passed, ProbeState.Passed, ProbeState.Failed, ServiceAvailability.Limited)]
    [InlineData(ProbeState.Passed, ProbeState.Unknown, ProbeState.Unknown, ProbeState.Unknown, ServiceAvailability.Limited)]
    public void LabelsReflectCheckedWebStagesOnly(ProbeState dns, ProbeState tcp, ProbeState tls, ProbeState http, ServiceAvailability expected)
    {
        var result = Result(dns, tcp, tls, http);
        Assert.Equal(expected, ServiceAvailabilityPolicy.Evaluate(result));
        Assert.Equal(ProbeState.Unknown, result.Voice.State); Assert.Equal(ProbeState.Unknown, result.MediaPlayback.State);
        Assert.Equal(ProbeState.Unknown, result.Login.State); Assert.Equal(ProbeState.Unknown, result.Stun.State);
    }
    [Fact]
    public void NoResultIsUnknownAndAllThreeTargetsAreFixedHttpsEndpoints()
    {
        Assert.Equal(ServiceAvailability.Unknown, ServiceAvailabilityPolicy.Evaluate(null));
        Assert.Equal(new[] { "youtube", "discord", "telegram" }, ServiceProbeService.Targets.Select(target => target.Id));
        Assert.All(ServiceProbeService.Targets, target => Assert.Equal("https", target.HttpsUrl.Scheme));
        Assert.Equal("web.telegram.org", ServiceProbeService.Targets[2].HttpsUrl.Host);
    }
    private sealed class Handler(HttpStatusCode code, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(json), RequestMessage = request });
    }
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}", AppUpdateState.NoPublishedUpdate)]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"v0.6.0\"}", AppUpdateState.UpToDate)]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"v0.5.0\"}", AppUpdateState.UpToDate)]
    [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"v0.7.0\"}", AppUpdateState.UpdateAvailable)]
    public async Task UpdateChecksCompareCurrentProductVersion(HttpStatusCode code, string json, AppUpdateState expected)
    {
        using var http = new HttpClient(new Handler(code, json));
        Assert.Equal(expected, (await new UpdateChecker(http).CheckAsync()).State);
    }
}
