using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;
// Inputs the contract fuzzer found to cause 500s or bodiless errors (Stage 7.1), now refused properly.

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
