using System.Net;
using System.Text.Json;
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

[Collection(ApiTestGroup.Name)]
public class EmptyQueryValueTests(ApiFactory factory)
{
    // An empty value is not one of the allowed values, so it is refused rather than quietly meaning "the default".
    // (The contract fuzzer found sort= accepted; the others are checked here so no other seed finds them.)
    [Theory]
    [InlineData("sort=")]
    [InlineData("sort=bogus")]
    [InlineData("type=")]
    [InlineData("type=article&type=")]
    [InlineData("tag=")]
    [InlineData("favourite=")]
    [InlineData("archived=")]
    [InlineData("created_from=")]
    [InlineData("created_to=")]
    [InlineData("page=")]
    [InlineData("page_size=")]
    public async Task An_empty_or_unknown_value_for_a_list_filter_is_a_validation_error(string query)
    {
        var c = await factory.UserAsync();
        var r = await c.GetAsync("/api/v1/resources?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("validation_failed", (await r.Json()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("q=")]
    [InlineData("collection_id=")]
    [InlineData("sort=newest&type=article&page=1&page_size=20&archived=false")]
    public async Task Parameters_that_may_be_empty_or_left_out_still_work(string query)
    {
        var c = await factory.UserAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/resources?" + query)).StatusCode);
    }
}

[Collection(ApiTestGroup.Name)]
public class TagFilterTests(ApiFactory factory)
{
    private static string Query(IEnumerable<string> tags) => string.Join('&', tags.Select(t => "tag=" + Uri.EscapeDataString(t)));

    [Fact]
    public async Task A_filter_with_more_tags_than_a_resource_can_have_is_refused()
    {
        // Contract fuzzing found a request with ~40 tag parameters taking 40 seconds, and 80 taking half a minute before failing.
        var c = await factory.UserAsync();
        var r = await c.GetAsync("/api/v1/resources?" + Query(Enumerable.Range(1, 21).Select(i => $"t{i}")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.True((await r.Json()).GetProperty("errors").TryGetProperty("tag", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/resources?" + Query(Enumerable.Range(1, 5000).Select(i => $"t{i}")))).StatusCode);
    }

    [Fact]
    public async Task Filtering_by_the_most_tags_allowed_is_quick_and_still_means_all_of_them()
    {
        var c = await factory.UserAsync();
        var all = Enumerable.Range(1, 20).Select(i => $"t{i}").ToArray();
        var both = await c.CreateResourceAsync("https://example.com/all", new { tags = all });
        await c.CreateResourceAsync("https://example.com/most", new { tags = all.Take(19).ToArray() });
        await c.CreateResourceAsync("https://example.com/none");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = await c.GetAsync("/api/v1/resources?" + Query(all)).ExpectAsync(HttpStatusCode.OK);
        watch.Stop();

        Assert.Equal([both.Id()], found.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed.TotalSeconds:F1} s");
        // The same name twice is one tag, not two.
        var twice = await c.GetAsync("/api/v1/resources?" + Query(["t1", "t1", "t2"])).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, twice.GetProperty("total").GetInt32());
    }
}

[Collection(ApiTestGroup.Name)]
public class BodyValueTests(ApiFactory factory)
{
    [Theory]
    [InlineData("")]
    [InlineData("pink")]
    public async Task A_collection_colour_that_is_not_one_of_the_colours_is_refused_including_an_empty_one(string color)
    {
        var c = await factory.UserAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/v1/collections", new { name = "C" + Guid.NewGuid().ToString("N")[..8], color })).StatusCode);
        var col = await c.CreateCollectionAsync("Patchable");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PatchAsync("/api/v1/collections/" + col.Id(), new { color })).StatusCode);
    }

    [Fact]
    public async Task No_colour_is_null_or_left_out_and_a_real_one_is_kept()
    {
        var c = await factory.UserAsync();
        var none = await c.PostAsync("/api/v1/collections", new { name = "NoColour", color = (string?)null }).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("color").ValueKind);
        var teal = await c.PostAsync("/api/v1/collections", new { name = "Teal", color = "teal" }).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("teal", teal.GetProperty("color").GetString());
        var cleared = await c.PatchAsync("/api/v1/collections/" + teal.Id(), new { color = (string?)null }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("color").ValueKind);
    }

    [Theory]
    [InlineData("~")]
    [InlineData("")]
    public async Task Invalid_names_in_ai_tags_are_refused_on_update_even_when_tags_is_not_sent(string bad)
    {
        var c = await factory.UserAsync();
        var r = await c.CreateResourceAsync("https://example.com/ai-tags");
        var response = await c.PatchAsync("/api/v1/resources/" + r.Id(), new { is_favourite = true, ai_tags = new[] { bad } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Json()).GetProperty("errors").TryGetProperty("ai_tags", out _));
    }
}
