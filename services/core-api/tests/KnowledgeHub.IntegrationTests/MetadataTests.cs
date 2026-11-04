using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>A fetcher tests can script, so no test touches the internet.</summary>
public sealed class ScriptedFetcher : IMetadataFetcher
{
    public ConcurrentQueue<string> Urls { get; } = new();
    public Func<string, MetadataResult> Behaviour { get; set; } = _ => MetadataResult.Ok(Page());

    public static PageMetadata Page(string title = "Fetched title", string? text = "Some article text.", ResourceType type = ResourceType.Article) =>
        new(title, "Fetched description", "https://img.example/c.png", "https://img.example/f.ico", "Example", type, text);

    public Task<MetadataResult> FetchAsync(string url, CancellationToken ct)
    {
        Urls.Enqueue(url);
        return Task.FromResult(Behaviour(url));
    }
}

[Collection(ApiTestGroup.Name)]
public class MetadataTests(ApiFactory factory)
{
    private sealed class Host(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> web, ScriptedFetcher fetcher) : IDisposable
    {
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Web { get; } = web;
        public ScriptedFetcher Fetcher { get; } = fetcher;
        public OutboxProcessor Processor => Web.Services.GetRequiredService<OutboxProcessor>();
        public void Dispose() => Web.Dispose();
    }

    private async Task<(Host Host, TestClient Client)> ArrangeAsync(Action<IWebHostBuilder>? configure = null)
    {
        await using (var db = factory.Db.Admin())
            await db.Outbox.Where(m => m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Processing)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var fetcher = new ScriptedFetcher();
        var web = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Outbox:Enabled", "false");
            b.ConfigureServices(s => s.AddSingleton<IMetadataFetcher>(fetcher));
            configure?.Invoke(b);
        });
        var client = new TestClient(web);
        Assert.Equal(HttpStatusCode.Created, (await client.RegisterAsync(TestClient.UniqueEmail())).StatusCode);
        return (new Host(web, fetcher), client);
    }

    /// <summary>Runs the worker until nothing is due, making retries due straight away.</summary>
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

    private static Task<JsonElement> GetResource(TestClient c, JsonElement r) =>
        c.GetAsync("/api/v1/resources/" + r.GetProperty("id").GetString()).ExpectAsync(HttpStatusCode.OK);

    private async Task<List<JsonElement>> ContentEvents(string resourceId)
    {
        await using var db = factory.Db.Admin();
        var rows = await db.Outbox.AsNoTracking().Where(m => m.Type == OutboxEventTypes.ResourceContentChanged)
            .OrderBy(m => m.CreatedAt).ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(m => JsonDocument.Parse(m.Payload).RootElement.Clone())
            .Where(p => p.TryGetProperty("resource_id", out var rid) && rid.GetString() == resourceId).ToList();
    }

    // ---------- the background job ----------

    [Fact]
    public async Task A_new_resource_gets_its_details_without_changing_updated_at()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var created = await c.CreateResourceAsync("https://example.com/post");
        Assert.Equal("pending", created.GetProperty("metadata_status").GetString());

        await DrainAsync(h);

        var r = await GetResource(c, created);
        Assert.Equal("ready", r.GetProperty("metadata_status").GetString());
        Assert.Equal("Fetched title", r.GetProperty("title").GetString());
        Assert.Equal("Fetched description", r.GetProperty("description").GetString());
        Assert.Equal("article", r.GetProperty("type").GetString());
        Assert.Equal("https://img.example/c.png", r.GetProperty("image_url").GetString());
        Assert.Equal("https://img.example/f.ico", r.GetProperty("favicon_url").GetString());
        Assert.Equal("Example", r.GetProperty("site_name").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("metadata_error").ValueKind);
        Assert.True(Math.Abs((created.GetProperty("updated_at").GetDateTimeOffset() - r.GetProperty("updated_at").GetDateTimeOffset()).TotalMilliseconds) < 1);
        Assert.Equal(["https://example.com/post"], h.Fetcher.Urls.ToArray());
    }

    [Fact]
    public async Task Fields_the_user_typed_are_never_overwritten()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var created = await c.CreateResourceAsync(extra: new { title = "My own title", type = "course" });

        await DrainAsync(h);

        var r = await GetResource(c, created);
        Assert.Equal("My own title", r.GetProperty("title").GetString());
        Assert.Equal("course", r.GetProperty("type").GetString());
        Assert.Equal("Fetched description", r.GetProperty("description").GetString());   // not typed, so filled in
    }

    [Fact]
    public async Task Editing_a_field_later_protects_it_from_a_retry()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var created = await c.CreateResourceAsync();
        await DrainAsync(h);
        var id = created.GetProperty("id").GetString();
        await c.PatchAsync($"/api/v1/resources/{id}", new { title = "Renamed", description = "Mine" }).ExpectAsync(HttpStatusCode.OK);

        await c.PostAsync($"/api/v1/resources/{id}/metadata/refresh").ExpectAsync(HttpStatusCode.Accepted);
        h.Fetcher.Behaviour = _ => MetadataResult.Ok(ScriptedFetcher.Page("Brand new title"));
        await DrainAsync(h);

        var r = await GetResource(c, created);
        Assert.Equal("Renamed", r.GetProperty("title").GetString());
        Assert.Equal("Mine", r.GetProperty("description").GetString());
        Assert.Equal("ready", r.GetProperty("metadata_status").GetString());
    }

    [Fact]
    public async Task Clearing_a_field_gives_it_back_to_the_fetcher()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var created = await c.CreateResourceAsync(extra: new { title = "Mine" });
        var id = created.GetProperty("id").GetString();
        await c.PatchAsync($"/api/v1/resources/{id}", new { title = (string?)null }).ExpectAsync(HttpStatusCode.OK);

        await DrainAsync(h);

        Assert.Equal("Fetched title", (await GetResource(c, created)).GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(MetadataError.BlockedUrl, "blocked_url")]
    [InlineData(MetadataError.NotHtml, "not_html")]
    [InlineData(MetadataError.TooLarge, "too_large")]
    [InlineData(MetadataError.TooManyRedirects, "too_many_redirects")]
    [InlineData(MetadataError.HttpError, "http_error")]
    public async Task A_permanent_failure_marks_the_resource_failed_at_once_without_retries(MetadataError error, string code)
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        h.Fetcher.Behaviour = _ => MetadataResult.Fail(error);
        var created = await c.CreateResourceAsync();

        await DrainAsync(h);

        var r = await GetResource(c, created);
        Assert.Equal("failed", r.GetProperty("metadata_status").GetString());
        Assert.Equal(code, r.GetProperty("metadata_error").GetString());
        Assert.Single(h.Fetcher.Urls);
        await using var db = factory.Db.Admin();
        var row = await db.Outbox.AsNoTracking().Where(m => m.Type == OutboxEventTypes.ResourceCreated)
            .OrderByDescending(m => m.CreatedAt).FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutboxStatus.Done, row.Status);
    }

    [Fact]
    public async Task A_temporary_failure_is_retried_and_then_succeeds()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var calls = 0;
        h.Fetcher.Behaviour = _ => ++calls < 3 ? MetadataResult.Fail(MetadataError.Timeout, transient: true) : MetadataResult.Ok(ScriptedFetcher.Page());
        var created = await c.CreateResourceAsync();

        await DrainAsync(h);

        Assert.Equal(3, calls);
        Assert.Equal("ready", (await GetResource(c, created)).GetProperty("metadata_status").GetString());
    }

    [Fact]
    public async Task A_temporary_failure_that_never_clears_ends_as_failed_not_pending_forever()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        h.Fetcher.Behaviour = _ => MetadataResult.Fail(MetadataError.Unreachable, transient: true);
        var created = await c.CreateResourceAsync();

        await DrainAsync(h);

        var r = await GetResource(c, created);
        Assert.Equal("failed", r.GetProperty("metadata_status").GetString());
        Assert.Equal("unreachable", r.GetProperty("metadata_error").GetString());
        Assert.Equal(8, h.Fetcher.Urls.Count);   // OutboxOptions.MaxAttempts
    }

    [Fact]
    public async Task Repeating_a_message_fetches_only_once()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var created = await c.CreateResourceAsync();
        await DrainAsync(h);
        var id = created.GetProperty("id").GetString();
        // The same event arrives again (an at-least-once queue can do that).
        await using (var db = factory.Db.Admin())
        {
            db.Outbox.Add(new OutboxMessage
            {
                Type = OutboxEventTypes.ResourceCreated,
                Payload = $$"""{"resource_id":"{{id}}"}""",
                NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await DrainAsync(h);
        Assert.Single(h.Fetcher.Urls);
    }

    [Fact]
    public async Task A_deleted_resource_is_not_fetched()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var created = await c.CreateResourceAsync();
        await c.SendAsync(HttpMethod.Delete, "/api/v1/resources/" + created.GetProperty("id").GetString()).ExpectAsync(HttpStatusCode.NoContent);

        await DrainAsync(h);

        Assert.Empty(h.Fetcher.Urls);
    }

    [Fact]
    public async Task Changing_the_link_fetches_the_new_page_and_a_title_edit_alone_does_not()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var created = await c.CreateResourceAsync("https://example.com/one");
        var id = created.GetProperty("id").GetString();
        await DrainAsync(h);

        await c.PatchAsync($"/api/v1/resources/{id}", new { notes = "just a note" }).ExpectAsync(HttpStatusCode.OK);
        await DrainAsync(h);
        Assert.Single(h.Fetcher.Urls);

        var changed = await c.PatchAsync($"/api/v1/resources/{id}", new { url = "https://example.com/two" }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("pending", changed.GetProperty("metadata_status").GetString());
        h.Fetcher.Behaviour = _ => MetadataResult.Ok(ScriptedFetcher.Page("Second page"));
        await DrainAsync(h);

        Assert.Equal(["https://example.com/one", "https://example.com/two"], h.Fetcher.Urls.ToArray());
        Assert.Equal("Second page", (await GetResource(c, created)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Text_is_sent_for_indexing_only_when_it_changes()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var created = await c.CreateResourceAsync();
        var id = created.GetProperty("id").GetString()!;
        await DrainAsync(h);
        var first = await ContentEvents(id);
        Assert.Single(first);
        Assert.Equal("Some article text.", first[0].GetProperty("text").GetString());

        await c.PostAsync($"/api/v1/resources/{id}/metadata/refresh").ExpectAsync(HttpStatusCode.Accepted);
        await DrainAsync(h);
        Assert.Single(await ContentEvents(id));   // same text: nothing new

        await c.PostAsync($"/api/v1/resources/{id}/metadata/refresh").ExpectAsync(HttpStatusCode.Accepted);
        h.Fetcher.Behaviour = _ => MetadataResult.Ok(ScriptedFetcher.Page(text: "The page was rewritten."));
        await DrainAsync(h);
        Assert.Equal(2, (await ContentEvents(id)).Count);
    }

    // ---------- refresh ----------

    [Fact]
    public async Task Refresh_queues_another_attempt_and_clears_the_error()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        h.Fetcher.Behaviour = _ => MetadataResult.Fail(MetadataError.HttpError);
        var created = await c.CreateResourceAsync();
        await DrainAsync(h);
        var id = created.GetProperty("id").GetString();
        Assert.Equal("failed", (await GetResource(c, created)).GetProperty("metadata_status").GetString());

        h.Fetcher.Behaviour = _ => MetadataResult.Ok(ScriptedFetcher.Page());
        var accepted = await c.PostAsync($"/api/v1/resources/{id}/metadata/refresh").ExpectAsync(HttpStatusCode.Accepted);
        Assert.Equal("pending", accepted.GetProperty("metadata_status").GetString());
        Assert.Equal(JsonValueKind.Null, accepted.GetProperty("metadata_error").ValueKind);
        await DrainAsync(h);

        Assert.Equal("ready", (await GetResource(c, created)).GetProperty("metadata_status").GetString());
    }

    [Fact]
    public async Task Refresh_of_someone_elses_or_a_missing_resource_is_not_found()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var other = await factory.UserAsync();
        var theirs = await other.CreateResourceAsync();
        await c.PostAsync($"/api/v1/resources/{theirs.GetProperty("id").GetString()}/metadata/refresh").ExpectAsync(HttpStatusCode.NotFound);
        await c.PostAsync($"/api/v1/resources/{Guid.NewGuid()}/metadata/refresh").ExpectAsync(HttpStatusCode.NotFound);
        var anonymous = factory.NewClient();
        await anonymous.FetchCsrfAsync();
        await anonymous.PostAsync($"/api/v1/resources/{Guid.NewGuid()}/metadata/refresh").ExpectAsync(HttpStatusCode.Unauthorized);
    }

    // ---------- preview ----------

    [Fact]
    public async Task Preview_returns_what_could_be_read_and_saves_nothing()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var p = await c.PostAsync("/api/v1/metadata/preview", new { url = "https://example.com/post" }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Fetched title", p.GetProperty("title").GetString());
        Assert.Equal("Fetched description", p.GetProperty("description").GetString());
        Assert.Equal("https://img.example/c.png", p.GetProperty("image_url").GetString());
        Assert.Equal("https://img.example/f.ico", p.GetProperty("favicon_url").GetString());
        Assert.Equal("article", p.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("error").ValueKind);
        var list = await c.GetAsync("/api/v1/resources").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Preview_reports_a_failure_as_data_not_as_an_error_response()
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        h.Fetcher.Behaviour = _ => MetadataResult.Fail(MetadataError.BlockedUrl);
        var p = await c.PostAsync("/api/v1/metadata/preview", new { url = "http://169.254.169.254/" }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("blocked_url", p.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("title").ValueKind);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("")]
    public async Task Preview_rejects_what_is_not_a_web_link_without_fetching(string url)
    {
        var (h, c) = await ArrangeAsync();
        using var _ = h;
        var p = await c.PostAsync("/api/v1/metadata/preview", new { url }).ExpectAsync(HttpStatusCode.BadRequest);
        Assert.Equal("validation_failed", p.GetProperty("code").GetString());
        Assert.Empty(h.Fetcher.Urls);
    }

    [Fact]
    public async Task Preview_needs_a_session()
    {
        var (h, _) = await ArrangeAsync();
        using var _h = h;
        var anonymous = factory.NewClient();
        await anonymous.FetchCsrfAsync();
        await anonymous.PostAsync("/api/v1/metadata/preview", new { url = "https://example.com" }).ExpectAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Preview_is_rate_limited_per_user()
    {
        var (h, c) = await ArrangeAsync(b => b.UseSetting("Auth:RateLimits:PreviewPerUserPerMinute", "3"));
        using var _ = h;
        for (var i = 0; i < 3; i++)
            await c.PostAsync("/api/v1/metadata/preview", new { url = "https://example.com" }).ExpectAsync(HttpStatusCode.OK);
        var limited = await c.PostAsync("/api/v1/metadata/preview", new { url = "https://example.com" }).ExpectAsync(HttpStatusCode.TooManyRequests);
        Assert.Equal("rate_limited", limited.GetProperty("code").GetString());
        // Another user is not affected.
        var other = new TestClient(h.Web);
        await other.RegisterAsync(TestClient.UniqueEmail());
        await other.PostAsync("/api/v1/metadata/preview", new { url = "https://example.com" }).ExpectAsync(HttpStatusCode.OK);
    }
}
