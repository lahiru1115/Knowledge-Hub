using System.Net;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class SemanticSearchTests(ApiFactory factory)
{
    private async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Web, RecordingAi Ai, TestClient Client)> ArrangeAsync()
    {
        var ai = new RecordingAi();
        var web = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Outbox:Enabled", "false");
            b.ConfigureServices(s => s.AddSingleton<IAiClient>(ai));
        });
        var client = new TestClient(web);
        Assert.Equal(HttpStatusCode.Created, (await client.RegisterAsync(TestClient.UniqueEmail())).StatusCode);
        return (web, ai, client);
    }

    private static Guid Id(JsonElement e) => Guid.Parse(e.GetProperty("id").GetString()!);

    [Fact]
    public async Task Results_are_the_users_resources_in_the_ais_order_with_snippets_and_scores()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var first = await c.CreateResourceAsync(extra: new { title = "About cars", tags = new[] { "auto" } });
        var second = await c.CreateResourceAsync(extra: new { title = "About dogs" });
        ai.SearchAnswer = _ => [new(Id(second), "a puppy…", 0.031), new(Id(first), "an automobile…", 0.016)];

        var r = await c.GetAsync("/api/v1/search/semantic?q=vehicle&page_size=10").ExpectAsync(HttpStatusCode.OK);

        var items = r.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([Id(second), Id(first)], items.Select(i => Id(i.GetProperty("resource"))));
        Assert.Equal("a puppy…", items[0].GetProperty("snippet").GetString());
        Assert.Equal(0.031, items[0].GetProperty("score").GetDouble(), 6);
        Assert.Equal(["auto"], items[1].GetProperty("resource").GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal((1, 10, 2, false), (r.GetProperty("page").GetInt32(), r.GetProperty("page_size").GetInt32(), r.GetProperty("total").GetInt32(), r.GetProperty("fallback").GetBoolean()));
        Assert.Equal("vehicle", ai.Searches.Single().Query);
    }

    [Fact]
    public async Task Hits_for_deleted_archived_or_foreign_resources_are_dropped()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var keep = await c.CreateResourceAsync();
        var deleted = await c.CreateResourceAsync();
        var archived = await c.CreateResourceAsync();
        var foreign = await (await factory.UserAsync()).CreateResourceAsync();
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(deleted)}").ExpectAsync(HttpStatusCode.NoContent);
        await c.PatchAsync($"/api/v1/resources/{Id(archived)}", new { is_archived = true }).ExpectAsync(HttpStatusCode.OK);
        ai.SearchAnswer = _ => [new(Id(deleted), "x", 3), new(Id(archived), "x", 2), new(Id(foreign), "x", 1.5), new(Id(keep), "kept", 1), new(Guid.NewGuid(), "ghost", 0.5)];

        var r = await c.GetAsync("/api/v1/search/semantic?q=anything").ExpectAsync(HttpStatusCode.OK);

        Assert.Equal([Id(keep)], r.GetProperty("items").EnumerateArray().Select(i => Id(i.GetProperty("resource"))));
        Assert.Equal(1, r.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task It_asks_the_ai_for_extra_hits_to_make_up_for_dropped_ones_but_returns_no_more_than_the_page_size()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var made = new List<Guid>();
        for (var i = 0; i < 4; i++) made.Add(Id(await c.CreateResourceAsync()));
        ai.SearchAnswer = _ => [.. made.Select((m, i) => new AiSearchHit(m, "s", 1 - i * 0.1))];

        var r = await c.GetAsync("/api/v1/search/semantic?q=x&page_size=3").ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(3, r.GetProperty("items").GetArrayLength());
        Assert.Equal(6, ai.Searches.Single().Limit);
    }

    [Fact]
    public async Task When_the_ai_service_is_down_it_falls_back_to_keyword_search_and_says_so()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        await c.CreateResourceAsync(extra: new { title = "Postgres connection pooling guide" });
        await c.CreateResourceAsync(extra: new { title = "Sourdough baking" });
        ai.Failure = () => new AiUnavailableException("down");

        var r = await c.GetAsync("/api/v1/search/semantic?q=pooling").ExpectAsync(HttpStatusCode.OK);

        Assert.True(r.GetProperty("fallback").GetBoolean());
        var titles = r.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("resource").GetProperty("title").GetString()).ToList();
        Assert.Equal(["Postgres connection pooling guide"], titles);
        Assert.Equal(0, r.GetProperty("items")[0].GetProperty("score").GetDouble());
    }

    [Fact]
    public async Task A_quota_refusal_is_reported_not_hidden_by_the_fallback()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        ai.Failure = () => new AiRejectedException(ProblemCode.AiQuotaExceeded, DateTimeOffset.UtcNow.AddHours(2), 7200);
        var response = await c.GetAsync("/api/v1/search/semantic?q=x");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("ai_quota_exceeded", (await response.Json()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("q=")]
    [InlineData("q=%20%20")]
    [InlineData("q=ok&page_size=0")]
    [InlineData("q=ok&page_size=51")]
    public async Task Bad_queries_are_validation_errors_and_never_reach_the_ai(string query)
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/search/semantic?" + query)).StatusCode);
        Assert.Empty(ai.Searches);
    }

    [Fact]
    public async Task A_query_of_501_characters_is_refused_and_500_is_fine()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/search/semantic?q=" + new string('a', 501))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/search/semantic?q=" + new string('a', 500))).StatusCode);
    }

    // ---------- related ----------

    [Fact]
    public async Task Related_returns_other_resources_in_order_at_most_five()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var me = await c.CreateResourceAsync();
        var others = new List<Guid>();
        for (var i = 0; i < 7; i++) others.Add(Id(await c.CreateResourceAsync()));
        ai.RelatedAnswer = _ => [.. others.Select((o, i) => new AiRelatedHit(o, 0.9 - i * 0.05)), new AiRelatedHit(Id(me), 1)];

        var r = await c.GetAsync($"/api/v1/resources/{Id(me)}/related").ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(others.Take(5), r.GetProperty("items").EnumerateArray().Select(Id));
        Assert.Equal(Id(me), ai.RelatedCalls.Single().Resource);
    }

    [Fact]
    public async Task Related_skips_deleted_and_archived_and_shows_nothing_of_other_users()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var me = await c.CreateResourceAsync();
        var ok = await c.CreateResourceAsync();
        var gone = await c.CreateResourceAsync();
        var foreign = await (await factory.UserAsync()).CreateResourceAsync();
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(gone)}").ExpectAsync(HttpStatusCode.NoContent);
        ai.RelatedAnswer = _ => [new(Id(gone), 0.9), new(Id(foreign), 0.8), new(Id(ok), 0.7)];

        var r = await c.GetAsync($"/api/v1/resources/{Id(me)}/related").ExpectAsync(HttpStatusCode.OK);

        Assert.Equal([Id(ok)], r.GetProperty("items").EnumerateArray().Select(Id));
    }

    [Fact]
    public async Task Related_for_someone_elses_resource_is_not_found_and_the_ai_is_not_asked()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var theirs = await (await factory.UserAsync()).CreateResourceAsync();
        await c.GetAsync($"/api/v1/resources/{Id(theirs)}/related").ExpectAsync(HttpStatusCode.NotFound);
        await c.GetAsync($"/api/v1/resources/{Guid.NewGuid()}/related").ExpectAsync(HttpStatusCode.NotFound);
        Assert.Empty(ai.RelatedCalls);
    }

    [Fact]
    public async Task Related_is_503_when_the_ai_service_is_down()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var me = await c.CreateResourceAsync();
        ai.Failure = () => new AiUnavailableException("down");
        var response = await c.GetAsync($"/api/v1/resources/{Id(me)}/related");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task Both_endpoints_need_a_session()
    {
        var (web, _, _) = await ArrangeAsync();
        using var _w = web;
        var anon = factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/search/semantic?q=x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/resources/{Guid.NewGuid()}/related")).StatusCode);
    }
}
