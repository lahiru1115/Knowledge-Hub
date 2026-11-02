using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Outbox;
using KnowledgeHub.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>A handler tests can script: record calls, fail on demand, take a while.</summary>
public sealed class ScriptedHandler : IOutboxHandler
{
    public const string Type = "test.event";
    public IReadOnlyCollection<string> Types { get; } = [Type, "test.other"];
    public ConcurrentQueue<OutboxContext> Calls { get; } = new();
    public ConcurrentDictionary<Guid, int> RunsPerMessage { get; } = new();
    public ConcurrentDictionary<Guid, string?> ParentIds { get; } = new();
    public Func<OutboxContext, Task>? Behaviour { get; set; }

    public async Task HandleAsync(OutboxContext context, CancellationToken ct)
    {
        Calls.Enqueue(context);
        RunsPerMessage.AddOrUpdate(context.MessageId, 1, (_, n) => n + 1);
        ParentIds[context.MessageId] = Activity.Current?.ParentId;
        if (Behaviour is not null) await Behaviour(context);
    }
}

[Collection(ApiTestGroup.Name)]
public class OutboxTests(ApiFactory factory)
{
    /// <summary>A host with the real worker off, so tests call the processor themselves, plus a scripted handler.</summary>
    private sealed class Harness(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, ScriptedHandler handler) : IDisposable
    {
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; } = factory;
        public ScriptedHandler Handler { get; } = handler;
        public OutboxProcessor Processor => Factory.Services.GetRequiredService<OutboxProcessor>();
        public void Dispose() => Factory.Dispose();
    }

    private async Task<Harness> ArrangeAsync(Action<IWebHostBuilder>? configure = null)
    {
        // Other tests leave events behind (the real handlers are not registered in every test); clear them so the
        // batch of 10 holds only what this test inserts. Tests in this collection run one at a time.
        await using (var db = factory.Db.Admin())
            await db.Outbox.Where(m => m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Processing).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var handler = new ScriptedHandler();
        var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Outbox:Enabled", "false");
            b.UseSetting("Outbox:BaseBackoffSeconds", "0.05");
            b.ConfigureServices(s => s.AddSingleton<IOutboxHandler>(handler));
            configure?.Invoke(b);
        });
        return new Harness(host, handler);
    }

    private async Task<Guid> InsertAsync(string type = ScriptedHandler.Type, string payload = "{}", DateTimeOffset? due = null, string? traceparent = null,
        OutboxStatus status = OutboxStatus.Pending, DateTimeOffset? lockedAt = null, int attempts = 0)
    {
        await using var db = factory.Db.Admin();
        var now = DateTimeOffset.UtcNow;
        var m = new OutboxMessage { Type = type, Payload = payload, Traceparent = traceparent, NextAttemptAt = due ?? now.AddSeconds(-1), CreatedAt = now, Status = status, LockedAt = lockedAt, Attempts = attempts };
        db.Outbox.Add(m);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return m.Id;
    }

    private async Task<OutboxMessage> GetAsync(Guid id)
    {
        await using var db = factory.Db.Admin();
        return await db.Outbox.AsNoTracking().SingleAsync(m => m.Id == id, TestContext.Current.CancellationToken);
    }

    // ---------- the processor ----------

    [Fact]
    public async Task A_successful_job_is_marked_done_with_the_time_and_no_error()
    {
        using var h = await ArrangeAsync();
        var (handler, processor) = (h.Handler, h.Processor);
        var id = await InsertAsync(payload: """{"hello":"world"}""");

        var claimed = await processor.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(claimed >= 1);
        var m = await GetAsync(id);
        Assert.Equal(OutboxStatus.Done, m.Status);
        Assert.NotNull(m.ProcessedAt);
        Assert.Null(m.LockedAt);
        Assert.Null(m.LastError);
        var call = handler.Calls.Single(c => c.MessageId == id);
        Assert.Equal("world", call.Payload.GetProperty("hello").GetString());
        Assert.Equal(1, call.Attempt);
        Assert.False(call.IsFinalAttempt);
    }

    [Fact]
    public async Task A_failing_job_is_retried_later_with_a_doubling_delay_and_the_error_is_kept()
    {
        using var h = await ArrangeAsync();
        var (handler, processor) = (h.Handler, h.Processor);
        handler.Behaviour = _ => throw new InvalidOperationException("boom");
        var id = await InsertAsync();

        await processor.RunOnceAsync(TestContext.Current.CancellationToken);

        var m = await GetAsync(id);
        Assert.Equal(OutboxStatus.Pending, m.Status);
        Assert.Equal(1, m.Attempts);
        Assert.Contains("boom", m.LastError, StringComparison.Ordinal);
        Assert.Null(m.LockedAt);
        Assert.True(m.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(-1));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(8, 1280)]
    public void Backoff_starts_at_ten_seconds_and_doubles_with_twenty_percent_jitter(int attempt, double expected)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new OutboxOptions());
        var processor = new OutboxProcessor(null!, null!, options, null!);
        for (var i = 0; i < 200; i++)
        {
            var d = processor.BackoffSeconds(attempt);
            Assert.InRange(d, expected * 0.8, expected * 1.2);
        }

        var samples = Enumerable.Range(0, 50).Select(_ => processor.BackoffSeconds(attempt)).Distinct().Count();
        Assert.True(samples > 1, "the delay should be jittered");
    }

    [Fact]
    public async Task Jobs_that_keep_failing_go_dead_after_eight_attempts_and_the_last_one_knows_it_is_final()
    {
        using var h = await ArrangeAsync();
        var (handler, processor) = (h.Handler, h.Processor);
        handler.Behaviour = _ => throw new InvalidOperationException("always");
        var id = await InsertAsync();

        for (var i = 0; i < 8; i++)
        {
            await processor.RunOnceAsync(TestContext.Current.CancellationToken);
            await using var db = factory.Db.Admin();
            await db.Outbox.Where(m => m.Id == id && m.Status == OutboxStatus.Pending).ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)), TestContext.Current.CancellationToken); // skip the wait
        }

        var m = await GetAsync(id);
        Assert.Equal(OutboxStatus.Dead, m.Status);
        Assert.Equal(8, m.Attempts);
        Assert.NotNull(m.LastError);
        var calls = handler.Calls.Where(c => c.MessageId == id).OrderBy(c => c.Attempt).ToList();
        Assert.Equal(Enumerable.Range(1, 8), calls.Select(c => c.Attempt));
        Assert.Equal([.. Enumerable.Repeat(false, 7), true], calls.Select(c => c.IsFinalAttempt));

        // A dead job is not picked up again, until someone sets it back to pending.
        var before = handler.Calls.Count;
        await processor.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before, handler.Calls.Count);
    }

    [Fact]
    public async Task A_job_that_is_not_due_yet_is_left_alone()
    {
        using var h = await ArrangeAsync();
        var (handler, processor) = (h.Handler, h.Processor);
        var id = await InsertAsync(due: DateTimeOffset.UtcNow.AddMinutes(5));
        await processor.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(handler.Calls, c => c.MessageId == id);
        Assert.Equal(OutboxStatus.Pending, (await GetAsync(id)).Status);
    }

    [Fact]
    public async Task An_event_type_nobody_handles_is_retried_and_ends_up_dead_with_a_clear_error()
    {
        using var h = await ArrangeAsync();
        var processor = h.Processor;
        var id = await InsertAsync(type: "nobody.handles.this");
        await processor.RunOnceAsync(TestContext.Current.CancellationToken);
        var m = await GetAsync(id);
        Assert.Equal(OutboxStatus.Pending, m.Status);
        Assert.Contains("No handler for 'nobody.handles.this'", m.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_workers_never_run_the_same_job()
    {
        using var h = await ArrangeAsync(b => b.UseSetting("Outbox:BatchSize", "10"));
        var (handler, processor) = (h.Handler, h.Processor);
        handler.Behaviour = _ => Task.Delay(30);
        var ids = new List<Guid>();
        for (var i = 0; i < 40; i++) ids.Add(await InsertAsync());

        // Several passes at once, as several processes would (FOR UPDATE SKIP LOCKED).
        for (var round = 0; round < 3; round++)
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => processor.RunOnceAsync(TestContext.Current.CancellationToken)));

        foreach (var id in ids) Assert.Equal(1, handler.RunsPerMessage.GetValueOrDefault(id));
        await using var db = factory.Db.Admin();
        Assert.All(await db.Outbox.Where(m => ids.Contains(m.Id)).ToListAsync(TestContext.Current.CancellationToken), m => Assert.Equal(OutboxStatus.Done, m.Status));
    }

    [Fact]
    public async Task A_batch_is_at_most_ten_jobs_per_pass()
    {
        using var h = await ArrangeAsync();
        var (handler, processor) = (h.Handler, h.Processor);
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++) ids.Add(await InsertAsync(type: "test.other", payload: $$"""{"n":{{i}}}"""));

        var claimed = await processor.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, claimed);
        Assert.Equal(10, handler.Calls.Count(c => ids.Contains(c.MessageId)));
    }

    [Fact]
    public async Task A_job_whose_worker_died_goes_back_to_pending_after_five_minutes_and_runs()
    {
        using var h = await ArrangeAsync();
        var (handler, processor) = (h.Handler, h.Processor);
        var fresh = await InsertAsync(status: OutboxStatus.Processing, lockedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var stuck = await InsertAsync(status: OutboxStatus.Processing, lockedAt: DateTimeOffset.UtcNow.AddMinutes(-6));

        await processor.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.RunsPerMessage.GetValueOrDefault(stuck));
        Assert.Equal(0, handler.RunsPerMessage.GetValueOrDefault(fresh)); // still plausibly running
        Assert.Equal(OutboxStatus.Processing, (await GetAsync(fresh)).Status);
        Assert.Equal(OutboxStatus.Done, (await GetAsync(stuck)).Status);
    }

    [Fact]
    public async Task The_job_runs_inside_the_trace_of_the_request_that_wrote_it()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "KnowledgeHub.Outbox",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var h = await ArrangeAsync();
        var (handler, processor) = (h.Handler, h.Processor);
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var traceparent = $"00-{traceId}-0123456789abcdef-01";
        var id = await InsertAsync(traceparent: traceparent);

        await processor.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(handler.ParentIds.TryGetValue(id, out var parent));
        Assert.Equal(traceparent, parent);
    }

    [Fact]
    public async Task Running_the_same_message_twice_is_safe_for_a_handler_that_is_idempotent()
    {
        // NFR-REL-03: the contract is that handlers can be re-run. Two deliveries of one message must not break the processor.
        using var h = await ArrangeAsync();
        var (handler, processor) = (h.Handler, h.Processor);
        var id = await InsertAsync();
        await processor.RunOnceAsync(TestContext.Current.CancellationToken);
        await using (var db = factory.Db.Admin())
            await db.Outbox.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, OutboxStatus.Pending), TestContext.Current.CancellationToken);

        await processor.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.RunsPerMessage[id]);
        Assert.Equal(OutboxStatus.Done, (await GetAsync(id)).Status);
    }

    // ---------- the background worker ----------

    [Fact]
    public async Task The_worker_picks_up_jobs_on_its_own()
    {
        using var h = await ArrangeAsync(b => { b.UseSetting("Outbox:Enabled", "true"); b.UseSetting("Outbox:PollSeconds", "0.1"); });
        var handler = h.Handler;
        _ = h.Factory.Server; // start the host, and with it the worker
        var id = await InsertAsync();

        await WaitAsync(async () => (await GetAsync(id)).Status == OutboxStatus.Done);

        Assert.Equal(1, handler.RunsPerMessage[id]);
    }

    [Fact]
    public async Task A_commit_that_wrote_an_outbox_row_wakes_the_worker_without_waiting_for_the_poll()
    {
        // The poll is 30 s, so only the in-memory signal can make this fast.
        using var h = await ArrangeAsync(b => { b.UseSetting("Outbox:Enabled", "true"); b.UseSetting("Outbox:PollSeconds", "30"); });
        var handler = h.Handler;
        _ = h.Factory.Server;
        await Task.Delay(300, TestContext.Current.CancellationToken); // the worker is now waiting out its 30 s poll
        await using var scope = h.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = new OutboxMessage { Type = ScriptedHandler.Type, NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1), CreatedAt = DateTimeOffset.UtcNow };
        db.Outbox.Add(message);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await WaitAsync(() => Task.FromResult(handler.RunsPerMessage.ContainsKey(message.Id)), timeoutMs: 5000);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "timed out waiting");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    // ---------- what the API writes ----------

    private async Task<List<(string Type, JsonElement Payload)>> EventsForAsync(Guid userId)
    {
        await using var db = factory.Db.Admin();
        var like = $"%{userId}%";
        var rows = await db.Outbox.FromSql($"SELECT * FROM core.outbox WHERE payload::text LIKE {like}").AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync(TestContext.Current.CancellationToken);
        return [.. rows.Select(r => (r.Type, JsonDocument.Parse(r.Payload).RootElement.Clone()))];
    }

    private static async Task<Guid> UserIdAsync(TestClient c) => Guid.Parse((await c.GetAsync("/api/v1/me").ExpectAsync(HttpStatusCode.OK)).Id());

    [Fact]
    public async Task Saving_a_resource_writes_a_created_event_in_the_same_transaction()
    {
        var c = await factory.UserAsync();
        var userId = await UserIdAsync(c);

        var r = await c.CreateResourceAsync();

        var created = Assert.Single(await EventsForAsync(userId));
        Assert.Equal("resource.created", created.Type);
        Assert.Equal(r.Id(), created.Payload.GetProperty("resource_id").GetString());
        Assert.Equal(userId.ToString(), created.Payload.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task A_change_and_its_event_are_saved_together_or_not_at_all()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var email = TestClient.UniqueEmail();
        var user = new User { Email = email, DisplayName = "x", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        db.Users.Add(new User { Email = email, DisplayName = "duplicate email", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var marker = Guid.NewGuid();
        outbox.Add("test.atomic", new { marker });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        await using var check = factory.Db.Admin();
        var like = $"%{marker}%";
        Assert.Empty(await check.Outbox.FromSql($"SELECT * FROM core.outbox WHERE payload::text LIKE {like}").ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Editing_writes_an_updated_event_only_when_something_indexed_changed()
    {
        var c = await factory.UserAsync();
        var userId = await UserIdAsync(c);
        var r = await c.CreateResourceAsync();

        await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { is_favourite = true, is_archived = true }).ExpectAsync(HttpStatusCode.OK);
        Assert.Single(await EventsForAsync(userId)); // only the created event

        await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { title = "New title" }).ExpectAsync(HttpStatusCode.OK);
        await c.PatchAsync($"/api/v1/resources/{r.Id()}", new { url = "https://example.com/elsewhere" }).ExpectAsync(HttpStatusCode.OK);

        var events = await EventsForAsync(userId);
        Assert.Equal(["resource.created", "resource.updated", "resource.updated"], events.Select(e => e.Type).ToArray());
        Assert.False(events[1].Payload.GetProperty("url_changed").GetBoolean());
        Assert.True(events[2].Payload.GetProperty("url_changed").GetBoolean());
    }

    [Fact]
    public async Task Delete_and_restore_write_events_for_single_and_bulk_actions()
    {
        var c = await factory.UserAsync();
        var userId = await UserIdAsync(c);
        var a = await c.CreateResourceAsync();
        var b = await c.CreateResourceAsync();
        var col = await c.CreateCollectionAsync("Box");

        await c.DeleteAsync($"/api/v1/resources/{a.Id()}").ExpectAsync(HttpStatusCode.NoContent);
        await c.PostAsync($"/api/v1/resources/{a.Id()}/restore").ExpectAsync(HttpStatusCode.OK);
        await c.PostAsync("/api/v1/resources/bulk", new { action = "delete", ids = new[] { a.Id(), b.Id() } }).ExpectAsync(HttpStatusCode.OK);
        await c.PostAsync("/api/v1/resources/bulk", new { action = "restore", ids = new[] { a.Id(), b.Id() } }).ExpectAsync(HttpStatusCode.OK);
        await c.PostAsync("/api/v1/resources/bulk", new { action = "move", ids = new[] { a.Id() }, collection_id = col.Id() }).ExpectAsync(HttpStatusCode.OK);

        var types = (await EventsForAsync(userId)).Select(e => e.Type).ToList();
        Assert.Equal(2, types.Count(t => t == "resource.created"));
        Assert.Equal(3, types.Count(t => t == "resource.deleted")); // one single, two bulk
        Assert.Equal(3, types.Count(t => t == "resource.restored"));
        Assert.Equal(1, types.Count(t => t == "resource.updated")); // the bulk move
    }

    [Fact]
    public async Task Deleting_a_collection_writes_events_for_the_resources_it_moved_or_deleted()
    {
        var c = await factory.UserAsync();
        var userId = await UserIdAsync(c);
        var one = await c.CreateCollectionAsync("One");
        var two = await c.CreateCollectionAsync("Two");
        await c.CreateResourceAsync(extra: new { collection_id = one.Id() });
        await c.CreateResourceAsync(extra: new { collection_id = two.Id() });

        await c.DeleteAsync($"/api/v1/collections/{one.Id()}?resources=move").ExpectAsync(HttpStatusCode.NoContent);
        await c.DeleteAsync($"/api/v1/collections/{two.Id()}?resources=delete").ExpectAsync(HttpStatusCode.NoContent);

        var types = (await EventsForAsync(userId)).Select(e => e.Type).ToList();
        Assert.Contains("resource.updated", types);
        Assert.Contains("resource.deleted", types);
    }

    [Fact]
    public async Task Deleting_the_account_queues_user_deleted_so_the_ai_data_can_be_purged()
    {
        var c = await factory.UserAsync();
        var userId = await UserIdAsync(c);
        await c.CreateResourceAsync();

        await c.SendAsync(HttpMethod.Delete, "/api/v1/me", new { password = "a long unusual passphrase" }).ExpectAsync(HttpStatusCode.NoContent);

        var deleted = (await EventsForAsync(userId)).Single(e => e.Type == "user.deleted");
        Assert.Equal(userId.ToString(), deleted.Payload.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task The_placeholder_handler_lets_the_ai_events_complete_until_the_ai_service_exists()
    {
        using var h = await ArrangeAsync();
        var processor = h.Processor;
        var ids = new List<Guid>();
        foreach (var type in new[] { "resource.content_changed", "resource.deleted", "resource.restored", "user.deleted" })
            ids.Add(await InsertAsync(type: type));

        await processor.RunOnceAsync(TestContext.Current.CancellationToken);

        foreach (var id in ids) Assert.Equal(OutboxStatus.Done, (await GetAsync(id)).Status);
    }
}
