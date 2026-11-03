using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
public class UnmatchedRouteTests(ApiFactory factory)
{
    [Theory]
    [InlineData("/api/v1/nothing-here")]
    [InlineData("/api/v1/resources/not-a-guid")]
    [InlineData("/api/v1/collections/null,null")]
    public async Task An_unknown_path_or_a_malformed_id_is_a_not_found_problem(string path)
    {
        var c = await factory.UserAsync();
        var r = await c.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal("not_found", (await r.Json()).GetProperty("code").GetString());
    }
}

[Collection(ApiTestGroup.Name)]
public class AwkwardInputTests(ApiFactory factory)
{
    [Theory]
    [InlineData("/api/v1/resources/check-url?url=http%3A%2F%2FE0%C3%A8%F3%96%B8%9D%F0%91%A2%97I%C3%AB")]
    public async Task A_link_with_an_impossible_host_is_a_validation_error_not_a_crash(string path)
    {
        var c = await factory.UserAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources", new { url = "http://E0è\U00016E1D\U00011897Ië" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/metadata/preview", new { url = "http://E0è\U00016E1D\U00011897Ië" })).StatusCode);
    }

    [Theory]
    [InlineData("created_from=2026-01-01T00:00:00%2B13:00")]
    [InlineData("created_to=2026-01-01T00:00:00-12:58")]
    [InlineData("created_from=0413-05-16T21:34:27.910097%2B00:00&created_to=2026-11-03T10:00:00%2B05:30")]
    public async Task Date_filters_in_any_time_zone_work(string query)
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/resources?" + query)).StatusCode);
    }

    [Fact]
    public async Task A_date_filter_in_another_zone_means_the_same_moment()
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync();
        var now = DateTimeOffset.UtcNow;
        // Same moment, written in +13:00: one hour after the resource was made it must not match, one hour before it must.
        string Q(DateTimeOffset t) => Uri.EscapeDataString(t.ToOffset(TimeSpan.FromHours(13)).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var after = await c.GetAsync("/api/v1/resources?created_from=" + Q(now.AddHours(1))).ExpectAsync(HttpStatusCode.OK);
        var before = await c.GetAsync("/api/v1/resources?created_from=" + Q(now.AddHours(-1))).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((0, 1), (after.GetProperty("total").GetInt32(), before.GetProperty("total").GetInt32()));
    }
}

[Collection(ApiTestGroup.Name)]
public class NullCharacterTests(ApiFactory factory)
{
    [Fact]
    public async Task A_null_character_in_a_body_or_query_is_a_validation_error_not_a_crash()
    {
        var c = await factory.UserAsync();
        foreach (var body in new object[] { new { name = "a\0b" }, new { name = "ok", description = "x\0" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/collections", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/resources", new { url = "https://example.com/a", notes = "n\0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/resources?q=a%00b")).StatusCode);
        var r = await c.PostAsync("/api/v1/collections", new { name = "x\0" });
        Assert.Equal("validation_failed", (await r.Json()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_escaped_backslash_before_u0000_is_ordinary_text()
    {
        var c = await factory.UserAsync();
        // The JSON text \\u0000 is a backslash followed by "u0000": nothing to refuse.
        var r = await c.SendRawJsonAsync(HttpMethod.Post, "/api/v1/collections", "{\"name\":\"back\\\\u0000slash\"}");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task Big_pages_are_refused_instead_of_overflowing()
    {
        var c = await factory.UserAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/resources?page=100001")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/resources?page=9223372036854775808")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/resources?page=100000&page_size=100")).StatusCode);
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
