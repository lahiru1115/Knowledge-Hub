using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class SecurityHeadersTests(ApiFactory factory)
{
    private static void AssertHeaders(HttpResponseMessage r, bool hsts)
    {
        Assert.Equal("nosniff", Assert.Single(r.Headers.GetValues("X-Content-Type-Options")));
        Assert.StartsWith("default-src 'none'", Assert.Single(r.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", r.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("strict-origin-when-cross-origin", Assert.Single(r.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("camera=()", Assert.Single(r.Headers.GetValues("Permissions-Policy")), StringComparison.Ordinal);
        Assert.Equal(hsts, r.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Answers_carry_the_security_headers_whether_they_succeed_or_fail()
    {
        var c = await factory.UserAsync();
        AssertHeaders(await c.GetAsync("/api/v1/me"), hsts: true);             // success
        AssertHeaders(await c.GetAsync("/api/v1/resources/not-a-guid"), hsts: true);   // routing failure
        AssertHeaders(await factory.NewClient().GetAsync("/api/v1/me"), hsts: true);   // 401
        AssertHeaders(await c.GetAsync("/api/v1/admin/users"), hsts: true);             // 403
        AssertHeaders(await c.GetAsync("/health/live"), hsts: true);
    }

    [Fact]
    public async Task Downloads_and_problems_keep_their_own_headers_next_to_them()
    {
        var c = await factory.UserAsync();
        var export = await c.GetAsync("/api/v1/export?format=csv");
        AssertHeaders(export, hsts: true);
        Assert.True(export.Headers.CacheControl!.NoStore);
        var bad = await c.GetAsync("/api/v1/export?format=xml");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        AssertHeaders(bad, hsts: true);
    }
}

[Collection(ApiTestGroup.Name)]
public class ForwardedHeadersTests(ApiFactory factory)
{
    private static async Task<HttpStatusCode> Register(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> web, string forwarded)
    {
        var c = new TestClient(web);
        c.DefaultHeaders["X-Forwarded-For"] = forwarded;
        c.DefaultHeaders["X-Forwarded-Proto"] = "https";
        return (await c.RegisterAsync(TestClient.UniqueEmail())).StatusCode;
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_real_client_gets_its_own_rate_limit_and_audit_address()
    {
        using var web = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Proxy:TrustForwardedHeaders", "true");
            b.UseSetting("Auth:RateLimits:RegisterPerIpPerHour", "1");
        });
        var a = $"203.0.113.{Random.Shared.Next(1, 250)}";
        var b2 = $"198.51.100.{Random.Shared.Next(1, 250)}";

        Assert.Equal(HttpStatusCode.Created, await Register(web, a));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Register(web, a));      // the same client is limited
        Assert.Equal(HttpStatusCode.Created, await Register(web, b2));              // another client is not
        // A client cannot pick its own address: only the last hop, the one the proxy added, is believed.
        Assert.Equal(HttpStatusCode.TooManyRequests, await Register(web, $"192.0.2.77, {a}"));

        await using var db = factory.Db.Admin();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Ip == IPAddress.Parse(b2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_the_setting_the_header_is_ignored()
    {
        using var web = factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimits:RegisterPerIpPerHour", "1"));
        Assert.Equal(HttpStatusCode.Created, await Register(web, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Register(web, "203.0.113.2"));   // same real connection, so same limit
    }
}
