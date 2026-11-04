using System.Net;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class AiFeaturesTests(ApiFactory factory)
{
    private sealed class Host(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> web, RecordingAi ai, ScriptedFetcher fetcher) : IDisposable
    {
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Web { get; } = web;
        public RecordingAi Ai { get; } = ai;
        public ScriptedFetcher Fetcher { get; } = fetcher;
        public OutboxProcessor Processor => Web.Services.GetRequiredService<OutboxProcessor>();
        public void Dispose() => Web.Dispose();
    }

    private async Task<(Host Host, TestClient Client)> ArrangeAsync()
    {
        await using (var db = factory.Db.Admin())
            await db.Outbox.Where(m => m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Processing)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var ai = new RecordingAi();
        var fetcher = new ScriptedFetcher();
        var web = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Outbox:Enabled", "false");
            b.ConfigureServices(s =>
            {
                s.AddSingleton<IAiClient>(ai);
                s.AddSingleton<IMetadataFetcher>(fetcher);
            });
        });
        var client = new TestClient(web);
        Assert.Equal(HttpStatusCode.Created, (await client.RegisterAsync(TestClient.UniqueEmail())).StatusCode);
        return (new Host(web, ai, fetcher), client);
    }

    private async Task DrainAsync(Host h)
    {
        for (var i = 0; i < 12; i++)
        {
            await using (var db = factory.Db.Admin())
                await db.Outbox.Where(m => m.Status == OutboxStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)), TestContext.Current.CancellationToken);
            if (await h.Processor.RunOnceAsync(TestContext.Current.CancellationToken) == 0) return;
        }
    }

    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;
    private static Task<JsonElement> Get(TestClient c, JsonElement r) => c.GetAsync("/api/v1/resources/" + Id(r)).ExpectAsync(HttpStatusCode.OK);

    private static async Task<HttpResponseMessage> Raw(Task<HttpResponseMessage> call, HttpStatusCode status)
    {
        var response = await call;
        Assert.True(response.StatusCode == status, $"expected {status}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return response;
    }

    // ---------- summaries ----------

    [Fact]
    public async Task Summarise_stores_the_summary_with_its_source_and_counts_as_an_edit()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync(extra: new { title = "Pooling guide", description = "About pooling" });

        var r = await c.PostAsync($"/api/v1/resources/{Id(created)}/summary").ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("A generated summary.", r.GetProperty("summary").GetString());
        Assert.Equal("extracted", r.GetProperty("summary_source").GetString());
        Assert.True(r.GetProperty("updated_at").GetDateTimeOffset() > created.GetProperty("updated_at").GetDateTimeOffset());
        var call = Assert.Single(h.Ai.Summarized);
        Assert.Equal((Guid.Parse(Id(created)), "Pooling guide", "About pooling", (string?)null), (call.Resource!.Value, call.Title, call.Description, call.Text));

        h.Ai.SummaryAnswer = () => new AiSummary("Model words.", "ai", "claude");
        var again = await c.PostAsync($"/api/v1/resources/{Id(created)}/summary").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(("Model words.", "ai"), (again.GetProperty("summary").GetString(), again.GetProperty("summary_source").GetString()));
    }

    [Fact]
    public async Task Regenerating_replaces_a_summary_the_user_wrote_because_they_asked_for_it()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync();
        await c.PatchAsync($"/api/v1/resources/{Id(created)}", new { summary = "Mine" }).ExpectAsync(HttpStatusCode.OK);
        var r = await c.PostAsync($"/api/v1/resources/{Id(created)}/summary").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("extracted", r.GetProperty("summary_source").GetString());
    }

    [Fact]
    public async Task Summarise_reports_each_kind_of_ai_problem_with_its_own_status_and_headers()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync();
        var path = $"/api/v1/resources/{Id(created)}/summary";

        h.Ai.Failure = () => new AiRejectedException(ProblemCode.NotEnoughContent);
        var thin = await Raw(c.PostAsync(path), HttpStatusCode.UnprocessableEntity);
        Assert.Equal("not_enough_content", (await thin.Json()).GetProperty("code").GetString());

        var reset = new DateTimeOffset(2030, 1, 2, 0, 0, 0, TimeSpan.Zero);
        h.Ai.Failure = () => new AiRejectedException(ProblemCode.AiQuotaExceeded, reset, 3600);
        var quota = await Raw(c.PostAsync(path), HttpStatusCode.TooManyRequests);
        var body = await quota.Json();
        Assert.Equal("ai_quota_exceeded", body.GetProperty("code").GetString());
        Assert.Equal(reset, body.GetProperty("reset_at").GetDateTimeOffset());
        Assert.Equal("3600", quota.Headers.GetValues("Retry-After").Single());

        h.Ai.Failure = () => new AiUnavailableException("down");
        var down = await Raw(c.PostAsync(path), HttpStatusCode.ServiceUnavailable);
        Assert.Equal("ai_unavailable", (await down.Json()).GetProperty("code").GetString());
        Assert.Equal("30", down.Headers.GetValues("Retry-After").Single());

        // None of the failures changed the resource.
        Assert.Equal(JsonValueKind.Null, (await Get(c, created)).GetProperty("summary").ValueKind);
    }

    [Fact]
    public async Task Summarise_and_remove_only_work_on_your_own_resources_and_the_ai_service_is_not_asked()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var other = await factory.UserAsync();
        var theirs = await other.CreateResourceAsync();
        await c.PostAsync($"/api/v1/resources/{Id(theirs)}/summary").ExpectAsync(HttpStatusCode.NotFound);
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(theirs)}/summary").ExpectAsync(HttpStatusCode.NotFound);
        Assert.Empty(h.Ai.Summarized);
    }

    [Fact]
    public async Task A_summary_can_be_removed()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync();
        await c.PostAsync($"/api/v1/resources/{Id(created)}/summary").ExpectAsync(HttpStatusCode.OK);
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(created)}/summary").ExpectAsync(HttpStatusCode.NoContent);
        var r = await Get(c, created);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("summary").ValueKind);
        Assert.Equal("none", r.GetProperty("summary_source").GetString());
    }

    // ---------- tag suggestions ----------

    [Fact]
    public async Task Suggestions_for_a_saved_resource_send_the_users_own_tags_and_nobody_elses()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        await c.CreateResourceAsync(extra: new { tags = new[] { "databases", "postgres" } });
        await c.CreateResourceAsync(extra: new { tags = new[] { "postgres" } });
        var other = await factory.UserAsync();
        await other.CreateResourceAsync(extra: new { tags = new[] { "secret-tag" } });
        var target = await c.CreateResourceAsync(extra: new { title = "Pooling" });

        var r = await c.PostAsync("/api/v1/ai/tag-suggestions", new { resource_id = Id(target) }).ExpectAsync(HttpStatusCode.OK);

        Assert.True(r.GetProperty("ai_available").GetBoolean());
        var items = r.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("name").GetString(), i.GetProperty("existing").GetBoolean())).ToList();
        Assert.Equal([("databases", true), ("pooling", false)], items);
        var call = Assert.Single(h.Ai.TagCalls);
        Assert.Equal(Guid.Parse(Id(target)), call.Resource);
        Assert.Equal(["postgres", "databases"], call.Existing);   // most used first
        Assert.DoesNotContain("secret-tag", call.Existing);
    }

    [Fact]
    public async Task Suggestions_for_a_url_use_the_guarded_fetcher_and_send_only_an_excerpt()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        h.Fetcher.Behaviour = _ => MetadataResult.Ok(ScriptedFetcher.Page(text: new string('x', 5000)));

        await c.PostAsync("/api/v1/ai/tag-suggestions", new { url = "https://example.com/post" }).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(["https://example.com/post"], h.Fetcher.Urls.ToArray());
        var call = Assert.Single(h.Ai.TagCalls);
        Assert.Null(call.Resource);
        Assert.Equal("Fetched title", call.Title);
        Assert.Equal(2000, call.Text!.Length);
    }

    [Fact]
    public async Task A_url_that_cannot_be_fetched_gives_no_suggestions_without_bothering_the_ai_service()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        h.Fetcher.Behaviour = _ => MetadataResult.Fail(MetadataError.BlockedUrl);
        var r = await c.PostAsync("/api/v1/ai/tag-suggestions", new { url = "http://169.254.169.254/" }).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(r.GetProperty("items").EnumerateArray());
        Assert.True(r.GetProperty("ai_available").GetBoolean());
        Assert.Empty(h.Ai.TagCalls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"url":"https://example.com","resource_id":"0193a9a0-0000-7000-8000-000000000000"}""")]
    [InlineData("""{"url":"ftp://example.com"}""")]
    [InlineData("""{"url":"not a url"}""")]
    public async Task Suggestions_need_exactly_one_valid_source(string json)
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var response = await c.SendAsync(HttpMethod.Post, "/api/v1/ai/tag-suggestions", JsonDocument.Parse(json).RootElement);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(h.Ai.TagCalls);
    }

    [Fact]
    public async Task Suggestions_for_someone_elses_resource_are_not_found()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var theirs = await (await factory.UserAsync()).CreateResourceAsync();
        await c.PostAsync("/api/v1/ai/tag-suggestions", new { resource_id = Id(theirs) }).ExpectAsync(HttpStatusCode.NotFound);
        Assert.Empty(h.Ai.TagCalls);
    }

    [Fact]
    public async Task When_the_ai_service_is_down_suggestions_are_empty_and_say_so_but_saving_is_unaffected()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        h.Ai.Failure = () => new AiUnavailableException("down");
        var target = await c.CreateResourceAsync();
        var r = await c.PostAsync("/api/v1/ai/tag-suggestions", new { resource_id = Id(target) }).ExpectAsync(HttpStatusCode.OK);
        Assert.Empty(r.GetProperty("items").EnumerateArray());
        Assert.False(r.GetProperty("ai_available").GetBoolean());
    }

    [Fact]
    public async Task Suggestions_over_the_quota_are_429()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var target = await c.CreateResourceAsync();
        h.Ai.Failure = () => new AiRejectedException(ProblemCode.AiQuotaExceeded, DateTimeOffset.UtcNow.AddHours(3), 10800);
        var r = await Raw(c.PostAsync("/api/v1/ai/tag-suggestions", new { resource_id = Id(target) }), HttpStatusCode.TooManyRequests);
        Assert.Equal("ai_quota_exceeded", (await r.Json()).GetProperty("code").GetString());
    }

    // ---------- status and usage ----------

    [Fact]
    public async Task Status_and_usage_have_the_contract_shapes()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var status = await c.GetAsync("/api/v1/ai/status").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((true, "free", false), (status.GetProperty("available").GetBoolean(), status.GetProperty("profile").GetString(), status.GetProperty("chat_available").GetBoolean()));

        var usage = await c.GetAsync("/api/v1/me/ai-usage").ExpectAsync(HttpStatusCode.OK);
        var items = usage.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(("summarize", 2, 300), (items[0].GetProperty("operation").GetString(), items[0].GetProperty("used").GetInt32(), items[0].GetProperty("limit").GetInt32()));
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("limit").ValueKind);
        Assert.True(usage.GetProperty("reset_at").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Usage_is_503_when_the_ai_service_is_down_but_status_just_says_unavailable()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        h.Ai.Failure = () => new AiUnavailableException("down");
        h.Ai.StatusAnswer = () => new AiStatusInfo(false, "free", false);
        await Raw(c.GetAsync("/api/v1/me/ai-usage"), HttpStatusCode.ServiceUnavailable);
        var status = await c.GetAsync("/api/v1/ai/status").ExpectAsync(HttpStatusCode.OK);
        Assert.False(status.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Every_ai_endpoint_needs_a_session()
    {
        var (h, _) = await ArrangeAsync();
        using var host = h;
        var anon = factory.NewClient();
        await anon.FetchCsrfAsync();
        var id = Guid.NewGuid();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, $"/api/v1/resources/{id}/summary"), (HttpMethod.Delete, $"/api/v1/resources/{id}/summary"),
            (HttpMethod.Post, "/api/v1/ai/tag-suggestions"), (HttpMethod.Get, "/api/v1/ai/status"), (HttpMethod.Get, "/api/v1/me/ai-usage"),
        })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(method, path, method == HttpMethod.Post ? new { } : null)).StatusCode);
    }

    // ---------- automatic summary after save ----------

    [Fact]
    public async Task A_new_page_gets_a_summary_automatically_without_changing_updated_at()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync();

        await DrainAsync(h);

        var r = await Get(c, created);
        Assert.Equal(("A generated summary.", "extracted"), (r.GetProperty("summary").GetString(), r.GetProperty("summary_source").GetString()));
        Assert.True(Math.Abs((r.GetProperty("updated_at").GetDateTimeOffset() - created.GetProperty("updated_at").GetDateTimeOffset()).TotalMilliseconds) < 1);
        Assert.Single(h.Ai.Summarized);
    }

    [Fact]
    public async Task Automatic_summaries_respect_the_users_switch()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        await c.PatchAsync("/api/v1/me", new { preferences = new { ai_auto_summary = false } }).ExpectAsync(HttpStatusCode.OK);
        var created = await c.CreateResourceAsync();
        await DrainAsync(h);
        Assert.Empty(h.Ai.Summarized);
        Assert.Equal(JsonValueKind.Null, (await Get(c, created)).GetProperty("summary").ValueKind);
    }

    [Fact]
    public async Task An_automatic_summary_never_replaces_one_the_user_wrote()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync(extra: new { summary = (string?)null });
        await c.PatchAsync($"/api/v1/resources/{Id(created)}", new { summary = "Written by me" }).ExpectAsync(HttpStatusCode.OK);

        await DrainAsync(h);

        var r = await Get(c, created);
        Assert.Equal(("Written by me", "user"), (r.GetProperty("summary").GetString(), r.GetProperty("summary_source").GetString()));
        Assert.Empty(h.Ai.Summarized);
    }

    [Fact]
    public async Task Running_out_of_quota_or_text_means_no_summary_not_a_failed_job()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        h.Ai.Failure = null;
        var calls = 0;
        h.Ai.SummaryAnswer = () => { calls++; throw new AiRejectedException(ProblemCode.AiQuotaExceeded, DateTimeOffset.UtcNow.AddHours(1), 3600); };
        var created = await c.CreateResourceAsync();

        await DrainAsync(h);

        Assert.True(calls >= 1);
        Assert.Equal(JsonValueKind.Null, (await Get(c, created)).GetProperty("summary").ValueKind);
        await using var db = factory.Db.Admin();
        var waiting = await db.Outbox.Where(m => m.Status == OutboxStatus.Pending && m.Type == OutboxEventTypes.ResourceContentChanged).ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(waiting, m => m.Payload.Contains(Id(created), StringComparison.Ordinal));
    }

    [Fact]
    public async Task If_the_ai_service_goes_down_while_summarising_the_job_is_retried()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var down = true;
        h.Ai.SummaryAnswer = () => down ? throw new AiUnavailableException("down") : new AiSummary("Later summary.", "extracted", "extractive-v1");
        var created = await c.CreateResourceAsync();

        await h.Processor.RunOnceAsync(TestContext.Current.CancellationToken);
        await h.Processor.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, (await Get(c, created)).GetProperty("summary").ValueKind);

        down = false;
        await DrainAsync(h);
        Assert.Equal("Later summary.", (await Get(c, created)).GetProperty("summary").GetString());
    }
}
