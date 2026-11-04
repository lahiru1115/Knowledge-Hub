using System.Diagnostics;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Infrastructure.Outbox;

/// <summary>
/// One pass of the worker (Doc 02 §5.3): put back messages whose worker died, claim due messages with
/// <c>FOR UPDATE SKIP LOCKED</c> so several workers never run the same one, run them, and record the outcome. A failure
/// schedules a retry with exponential backoff and jitter; after the last attempt the message is dead.
/// </summary>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopes,
    IClock clock,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
{
    public static readonly ActivitySource Source = new("KnowledgeHub.Outbox");
    private readonly OutboxOptions _o = options.Value;

    private DateTimeOffset _backlogReadAt = DateTimeOffset.MinValue;

    /// <summary>Returns how many messages were claimed (so the caller can go again at once when the batch was full).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        List<OutboxMessage> claimed;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await ReclaimStuckAsync(db, ct);
            claimed = await ClaimAsync(db, ct);
        }

        // Different resources run side by side; messages for the same resource run one after another, oldest first, so
        // two index jobs for it never overlap and the newest content is the one written last.
        await Task.WhenAll(claimed
            .GroupBy(OrderingKey)
            .Select(async group =>
            {
                foreach (var m in group.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)) await ProcessAsync(m, ct);
            }));
        await RecordBacklogAsync(ct);
        return claimed.Count;
    }

    /// <summary>The resource a message is about, or the message itself when it is not about one.</summary>
    private static string OrderingKey(OutboxMessage m)
    {
        try
        {
            using var doc = JsonDocument.Parse(m.Payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("resource_id", out var r)
                && r.ValueKind == JsonValueKind.String)
                return "r:" + r.GetString();
        }
        catch (JsonException)
        {
            // An unreadable payload fails in ProcessAsync, where it is recorded; here it simply runs on its own.
        }

        return "m:" + m.Id;
    }

    /// <summary>The numbers behind <c>kh.outbox.pending</c> and <c>kh.outbox.dead</c> (NFR-OBS-03).</summary>
    private async Task RecordBacklogAsync(CancellationToken ct)
    {
        // A count over the outbox after every pass (every 2 seconds when idle) is more than a gauge needs.
        var now = clock.UtcNow;
        if (now - _backlogReadAt < TimeSpan.FromSeconds(_o.BacklogMetricSeconds)) return;
        _backlogReadAt = now;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var counts = await db.Outbox.AsNoTracking()
                .Where(m => m.Status == OutboxStatus.Pending || m.Status == OutboxStatus.Processing || m.Status == OutboxStatus.Dead)
                .GroupBy(m => m.Status == OutboxStatus.Dead).Select(g => new { Dead = g.Key, Count = g.Count() }).ToListAsync(ct);
            KhMetrics.SetOutboxBacklog(counts.FirstOrDefault(c => !c.Dead)?.Count ?? 0, counts.FirstOrDefault(c => c.Dead)?.Count ?? 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogBacklogFailed(e);   // a metric must never stop the worker
        }
    }

    private async Task ReclaimStuckAsync(AppDbContext db, CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddMinutes(-_o.StuckMinutes);
        var n = await db.Database.ExecuteSqlAsync(
            $"UPDATE core.outbox SET status = 'pending', locked_at = NULL WHERE status = 'processing' AND locked_at < {cutoff}", ct);
        if (n > 0) LogReclaimed(n);
    }

    private async Task<List<OutboxMessage>> ClaimAsync(AppDbContext db, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var batch = _o.BatchSize;
        return await db.Outbox.FromSql($"""
            UPDATE core.outbox SET status = 'processing', locked_at = {now}
            WHERE id IN (
                SELECT id FROM core.outbox
                WHERE status = 'pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at, id
                LIMIT {batch}
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);
    }

    private async Task ProcessAsync(OutboxMessage message, CancellationToken ct)
    {
        // The job joins the trace of the request that wrote it (NFR-OBS-02).
        ActivityContext parent = default;
        if (message.Traceparent is not null) ActivityContext.TryParse(message.Traceparent, null, out parent);
        using var activity = Source.StartActivity("outbox.process", ActivityKind.Consumer, parent);
        activity?.SetTag("outbox.type", message.Type);
        activity?.SetTag("outbox.attempt", message.Attempts + 1);

        var attempt = message.Attempts + 1;
        var started = KhMetrics.Start();
        try
        {
            using var scope = scopes.CreateScope();
            var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().Where(h => h.Types.Contains(message.Type)).ToList();
            if (handlers.Count == 0) throw new InvalidOperationException($"No handler for '{message.Type}'.");

            using var payload = JsonDocument.Parse(message.Payload);
            var context = new OutboxContext(message.Id, message.Type, payload.RootElement.Clone(), attempt, attempt >= _o.MaxAttempts);
            // Every handler gets its turn even if one before it fails: a message such as resource.created feeds both the
            // AI index and the metadata fetch, and the AI service being down must not stop the fetch. Handlers are
            // idempotent, so on a retry the ones that succeeded simply run again.
            List<Exception>? failures = null;
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.HandleAsync(context, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    (failures ??= []).Add(e);
                }
            }

            if (failures is [var only]) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(only);
            if (failures is { Count: > 1 }) throw new AggregateException(failures);
            await FinishAsync(message.Id, ct);
            KhMetrics.OutboxJobDuration.Record(KhMetrics.Seconds(started), Tag("type", message.Type), Tag("outcome", "ok"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: leave it claimed; it returns to pending after the stuck timeout.
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            KhMetrics.OutboxJobDuration.Record(KhMetrics.Seconds(started), Tag("type", message.Type),
                Tag("outcome", attempt >= _o.MaxAttempts ? "dead" : "retry"));
            await FailAsync(message, attempt, ex, ct);
        }
    }

    private async Task FinishAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = clock.UtcNow;
        await db.Outbox.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, OutboxStatus.Done)
            .SetProperty(m => m.ProcessedAt, now)
            .SetProperty(m => m.LockedAt, (DateTimeOffset?)null)
            .SetProperty(m => m.LastError, (string?)null), ct);
    }

    private async Task FailAsync(OutboxMessage message, int attempt, Exception ex, CancellationToken ct)
    {
        var dead = attempt >= _o.MaxAttempts;
        var now = clock.UtcNow;
        var next = now.AddSeconds(BackoffSeconds(attempt));
        var error = Truncate($"{ex.GetType().Name}: {ex.Message}", 2000);
        LogFailed(message.Type, message.Id, attempt, dead, ex);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Outbox.Where(m => m.Id == message.Id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, dead ? OutboxStatus.Dead : OutboxStatus.Pending)
            .SetProperty(m => m.Attempts, attempt)
            .SetProperty(m => m.NextAttemptAt, next)
            .SetProperty(m => m.LockedAt, (DateTimeOffset?)null)
            .SetProperty(m => m.LastError, error)
            .SetProperty(m => m.ProcessedAt, dead ? now : (DateTimeOffset?)null), ct);
    }

    /// <summary>Base delay doubling each attempt (10 s, 20 s, 40 s …) with 20 % jitter either way (NFR-REL-02).</summary>
    public double BackoffSeconds(int attempt)
    {
        var baseDelay = _o.BaseBackoffSeconds * Math.Pow(2, attempt - 1);
        return baseDelay * (0.8 + (Random.Shared.NextDouble() * 0.4));
    }

    private static KeyValuePair<string, object?> Tag(string key, object value) => new(key, value);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the outbox backlog for the metrics")]
    private partial void LogBacklogFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Put {Count} stuck outbox messages back to pending")]
    private partial void LogReclaimed(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {Type} {Id} failed on attempt {Attempt} (dead: {Dead})")]
    private partial void LogFailed(string type, Guid id, int attempt, bool dead, Exception exception);
}
