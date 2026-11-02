using System.Diagnostics;
using System.Text.Json;
using KnowledgeHub.Domain;

namespace KnowledgeHub.Application.Common;

/// <summary>
/// Queues background work. The message is added to the current unit of work, so it is saved (or not) together with
/// the change that caused it (DR-05).
/// </summary>
public interface IOutbox
{
    void Add(string type, object payload);
}

public sealed class OutboxWriter(IAppDbContext db, IClock clock) : IOutbox
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public void Add(string type, object payload)
    {
        var now = clock.UtcNow;
        db.Outbox.Add(new OutboxMessage
        {
            Type = type,
            Payload = JsonSerializer.Serialize(payload, s_json),
            Traceparent = Activity.Current?.Id,
            NextAttemptAt = now,
            CreatedAt = now,
        });
    }
}

/// <summary>What a handler gets: the message and how far along the retries are.</summary>
public sealed record OutboxContext(Guid MessageId, string Type, JsonElement Payload, int Attempt, bool IsFinalAttempt);

/// <summary>
/// Runs one kind of event. Handlers must be idempotent: processing a message twice gives the same result as once
/// (NFR-REL-03). Throwing means "try again later"; after the last attempt the message goes dead.
/// </summary>
public interface IOutboxHandler
{
    IReadOnlyCollection<string> Types { get; }

    Task HandleAsync(OutboxContext context, CancellationToken ct);
}
