namespace KnowledgeHub.Domain;

public enum OutboxStatus { Pending, Processing, Done, Dead }

/// <summary>The events the background worker handles (Doc 02 §5.3).</summary>
public static class OutboxEventTypes
{
    public const string ResourceCreated = "resource.created";
    public const string ResourceUpdated = "resource.updated";
    /// <summary>A deleted resource was removed for good, so the AI service can forget its text too.</summary>
    public const string ResourcePurged = "resource.purged";
    public const string ResourceContentChanged = "resource.content_changed";
    public const string ResourceDeleted = "resource.deleted";
    public const string ResourceRestored = "resource.restored";
    public const string UserDeleted = "user.deleted";
}

/// <summary>
/// A unit of background work, written in the same transaction as the change that needs it (DR-05, ADR-004). The
/// worker claims it, runs the matching handler, and retries with backoff until it succeeds or goes dead (NFR-REL-02).
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string Type { get; init; } = "";
    /// <summary>JSON with the IDs and the minimum data the job needs.</summary>
    public string Payload { get; init; } = "{}";
    /// <summary>W3C trace context of the request that wrote it, so the job joins the same trace (NFR-OBS-02).</summary>
    public string? Traceparent { get; init; }
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>When a worker claimed it; a claim older than 5 minutes means the worker died (Doc 02 §5.3).</summary>
    public DateTimeOffset? LockedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
