using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Maintenance;
using KnowledgeHub.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

public sealed class RecordingAi : IAiClient
{
    public ConcurrentQueue<string> Log { get; } = new();
    public ConcurrentQueue<(Guid User, Guid Resource, AiIndexRequest Request)> Indexed { get; } = new();
    public ConcurrentQueue<(Guid User, Guid Resource, bool Forget)> Deleted { get; } = new();
    public ConcurrentQueue<Guid> Purged { get; } = new();
    public Func<Exception?>? Failure { get; set; }

    // The user-facing calls, scripted per test.
    public ConcurrentQueue<(Guid User, Guid? Resource, string? Title, string? Description, string? Text)> Summarized { get; } = new();
    public Func<AiSummary> SummaryAnswer { get; set; } = () => new AiSummary("A generated summary.", "extracted", "extractive-v1");
    public ConcurrentQueue<(Guid User, Guid? Resource, string? Title, string? Text, IReadOnlyCollection<string> Existing)> TagCalls { get; } = new();
    public Func<IReadOnlyList<AiTagSuggestion>> TagAnswer { get; set; } = () => [new("databases", true), new("pooling", false)];
    public Func<AiUsageInfo> UsageAnswer { get; set; } = () => new(DateTimeOffset.UtcNow.Date.AddDays(1), [new("summarize", 2, 300), new("search", 0, null)]);
    public Func<AiStatusInfo> StatusAnswer { get; set; } = () => new(true, "free", false);

    public Task<AiSummary> SummarizeAsync(Guid userId, Guid? resourceId, string? title, string? description, string? text, CancellationToken ct)
    {
        if (Failure?.Invoke() is { } e) throw e;
        Summarized.Enqueue((userId, resourceId, title, description, text));
        return Task.FromResult(SummaryAnswer());
    }

    public Task<IReadOnlyList<AiTagSuggestion>> SuggestTagsAsync(
        Guid userId, Guid? resourceId, string? title, string? description, string? text,
        IReadOnlyCollection<string> existingTags, CancellationToken ct)
    {
        if (Failure?.Invoke() is { } e) throw e;
        TagCalls.Enqueue((userId, resourceId, title, text, existingTags));
        return Task.FromResult(TagAnswer());
    }

    public Task<AiUsageInfo> GetUsageAsync(Guid userId, CancellationToken ct)
    {
        if (Failure?.Invoke() is { } e) throw e;
        return Task.FromResult(UsageAnswer());
    }

    public Task<AiStatusInfo> GetStatusAsync(CancellationToken ct) => Task.FromResult(StatusAnswer());

    public Task IndexAsync(Guid userId, Guid resourceId, AiIndexRequest request, CancellationToken ct)
    {
        if (Failure?.Invoke() is { } e) throw e;
        Log.Enqueue("index");
        Indexed.Enqueue((userId, resourceId, request));
        return Task.CompletedTask;
    }

    public Task DeleteIndexAsync(Guid userId, Guid resourceId, bool forget, CancellationToken ct)
    {
        if (Failure?.Invoke() is { } e) throw e;
        Log.Enqueue(forget ? "forget" : "delete");
        Deleted.Enqueue((userId, resourceId, forget));
        return Task.CompletedTask;
    }

    public Task PurgeUserAsync(Guid userId, CancellationToken ct)
    {
        if (Failure?.Invoke() is { } e) throw e;
        Log.Enqueue("purge");
        Purged.Enqueue(userId);
        return Task.CompletedTask;
    }
}

[Collection(ApiTestGroup.Name)]
public class AiIndexTests(ApiFactory factory)
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

    private static Guid Id(JsonElement e) => Guid.Parse(e.GetProperty("id").GetString()!);

    [Fact]
    public async Task A_new_resource_is_indexed_with_its_fields_and_then_with_the_page_text()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync("https://example.com/a", new { notes = "my notes", tags = new[] { "x" } });

        await DrainAsync(h);

        var calls = h.Ai.Indexed.Where(i => i.Resource == Id(created)).ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, i => Assert.Equal("my notes", i.Request.Notes));
        var withText = Assert.Single(calls, i => i.Request.Text is not null);
        Assert.Equal("Some article text.", withText.Request.Text);
        Assert.Equal("Fetched title", withText.Request.Title);     // the title the metadata job filled in
        Assert.Equal("Fetched description", withText.Request.Description);
        Assert.Equal(calls.Select(i => i.User).Distinct().Single(), withText.User);
    }

    [Fact]
    public async Task An_edit_reindexes_without_text_and_carries_the_new_collection()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync();
        await DrainAsync(h);
        var col = await c.CreateCollectionAsync("Databases");
        while (h.Ai.Indexed.TryDequeue(out _)) { }

        await c.PatchAsync($"/api/v1/resources/{Id(created)}", new { title = "Renamed", collection_id = col.GetProperty("id").GetString() })
            .ExpectAsync(HttpStatusCode.OK);
        await DrainAsync(h);

        var call = Assert.Single(h.Ai.Indexed);
        Assert.Equal("Renamed", call.Request.Title);
        Assert.Equal(Guid.Parse(col.GetProperty("id").GetString()!), call.Request.CollectionId);
        Assert.Null(call.Request.Text);   // the AI service keeps the text it already has
    }

    [Fact]
    public async Task Deleting_and_restoring_remove_and_restore_the_index()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync();
        await DrainAsync(h);
        while (h.Ai.Log.TryDequeue(out _)) { }

        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(created)}").ExpectAsync(HttpStatusCode.NoContent);
        await DrainAsync(h);
        Assert.Equal(["delete"], h.Ai.Log.ToArray());
        Assert.Equal((Id(created), false), (h.Ai.Deleted.Single().Resource, h.Ai.Deleted.Single().Forget));

        await c.PostAsync($"/api/v1/resources/{Id(created)}/restore").ExpectAsync(HttpStatusCode.OK);
        await DrainAsync(h);
        Assert.Equal(["delete", "index"], h.Ai.Log.ToArray());
    }

    [Fact]
    public async Task A_resource_deleted_before_its_event_runs_is_not_indexed()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync();
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(created)}").ExpectAsync(HttpStatusCode.NoContent);

        await DrainAsync(h);

        Assert.Empty(h.Ai.Indexed);
        Assert.Single(h.Ai.Deleted);
    }

    [Fact]
    public async Task Deleting_the_account_purges_the_users_data_in_the_ai_service()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var me = await c.GetAsync("/api/v1/me").ExpectAsync(HttpStatusCode.OK);
        await c.SendAsync(HttpMethod.Delete, "/api/v1/me", new { password = "a long unusual passphrase" }).ExpectAsync(HttpStatusCode.NoContent);

        await DrainAsync(h);

        Assert.Equal(Guid.Parse(me.GetProperty("id").GetString()!), Assert.Single(h.Ai.Purged));
    }

    [Fact]
    public async Task When_the_ai_service_is_down_the_job_waits_in_the_outbox_and_runs_when_it_is_back()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        h.Ai.Failure = () => new AiUnavailableException("down");
        var created = await c.CreateResourceAsync();

        await h.Processor.RunOnceAsync(TestContext.Current.CancellationToken);
        await using (var db = factory.Db.Admin())
        {
            var failing = await db.Outbox.AsNoTracking().Where(m => m.Type == OutboxEventTypes.ResourceCreated && m.Status == OutboxStatus.Pending && m.Attempts > 0).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains(failing, m => m.Payload.Contains(Id(created).ToString(), StringComparison.Ordinal));
            Assert.Contains("down", failing[0].LastError, StringComparison.Ordinal);
        }
        Assert.Empty(h.Ai.Indexed);

        h.Ai.Failure = null;   // the service is back
        await DrainAsync(h);
        Assert.Contains(h.Ai.Indexed, i => i.Resource == Id(created));
        // The page details were still fetched (the metadata job is independent of the index).
        Assert.Equal("ready", (await c.GetAsync($"/api/v1/resources/{Id(created)}").ExpectAsync(HttpStatusCode.OK)).GetProperty("metadata_status").GetString());
    }

    [Fact]
    public async Task Maintenance_tells_the_ai_service_to_forget_a_purged_resource()
    {
        var (h, c) = await ArrangeAsync();
        using var host = h;
        var created = await c.CreateResourceAsync();
        await DrainAsync(h);
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(created)}").ExpectAsync(HttpStatusCode.NoContent);
        await using (var db = factory.Db.Admin())
            await db.Resources.IgnoreQueryFilters().Where(r => r.Id == Id(created))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedAt, DateTimeOffset.UtcNow.AddDays(-8)), TestContext.Current.CancellationToken);
        while (h.Ai.Deleted.TryDequeue(out _)) { }

        await using (var scope = h.Web.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MaintenanceService>().RunOnceAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        await DrainAsync(h);

        var forgotten = h.Ai.Deleted.Where(d => d.Resource == Id(created)).ToList();
        Assert.Contains(forgotten, d => d.Forget);
        await using var check = factory.Db.Admin();
        Assert.False(await check.Resources.IgnoreQueryFilters().AnyAsync(r => r.Id == Id(created), TestContext.Current.CancellationToken));
    }
}
