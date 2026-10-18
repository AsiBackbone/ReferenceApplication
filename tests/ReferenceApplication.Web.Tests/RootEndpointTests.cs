using System.Net;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

public sealed class RootEndpointTests
{
    [Fact]
    public async Task RootEndpoint_ReturnsLandingPage()
    {
        using var factory = ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess();
        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("AsiBackbone Reference Application", body, StringComparison.Ordinal);
        Assert.Contains("NCAT + AsiBackbone Integration", body, StringComparison.Ordinal);
        Assert.Contains("4 of 6 walkthrough steps complete", body, StringComparison.Ordinal);
        Assert.Contains("Secure NCAT foundation", body, StringComparison.Ordinal);
        Assert.Contains("AsiBackbone integration", body, StringComparison.Ordinal);
        Assert.Contains("Integration walkthrough", body, StringComparison.Ordinal);
        Assert.Contains(
            "https://asibackbone.github.io/ReferenceApplication/walkthrough/",
            body,
            StringComparison.Ordinal);
        Assert.Contains("https://github.com/AsiBackbone/ReferenceApplication", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".NET Core Application Template", body, StringComparison.Ordinal);
        Assert.DoesNotContain("https://github.com/AsiBackbone/NetCoreApplicationTemplate", body, StringComparison.Ordinal);
        Assert.Contains("/css/landing.css", body, StringComparison.Ordinal);
    }
}
