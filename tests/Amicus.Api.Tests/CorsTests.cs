using System.Net.Http;
using Amicus.Infrastructure.Identity;

namespace Amicus.Api.Tests;

[Collection(AmicusCollection.Name)]
public sealed class CorsTests(AmicusFixture fixture)
{
    private readonly AmicusAppFactory _app = fixture.App;

    private async Task<HttpResponseMessage> WithOrigin(string origin)
    {
        var client = _app.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("Origin", origin);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task The_web_origin_is_allowed()
    {
        var res = await WithOrigin("https://app.thorsp.net");
        Assert.Equal("https://app.thorsp.net",
            Assert.Single(res.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task A_pages_preview_subdomain_is_allowed()
    {
        var res = await WithOrigin("https://abc123.amicus-web.pages.dev");
        Assert.Equal("https://abc123.amicus-web.pages.dev",
            Assert.Single(res.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task An_unknown_origin_gets_no_cors_header()
    {
        var res = await WithOrigin("https://evil.example.com");
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
