namespace KnowledgeHub.Application.Common;

/// <summary>What the AI service needs to index a resource. A null <see cref="Text"/> means "keep the stored text".</summary>
public sealed record AiIndexRequest(string? Title, string? Description, string? Notes, Guid? CollectionId, string? Text);

/// <summary>
/// The Core API's view of the AI service (Doc 02 §5.2). Every call is idempotent, so the outbox can repeat it.
/// Failures that are worth trying again later throw <see cref="AiUnavailableException"/>.
/// </summary>
public interface IAiClient
{
    Task IndexAsync(Guid userId, Guid resourceId, AiIndexRequest request, CancellationToken ct);

    /// <summary>Removes the resource's chunks. <paramref name="forget"/> also removes the stored text (after purge).</summary>
    Task DeleteIndexAsync(Guid userId, Guid resourceId, bool forget, CancellationToken ct);

    /// <summary>Removes everything the AI service holds for a user (DR-06).</summary>
    Task PurgeUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Summarises a page. With <paramref name="resourceId"/> and no <paramref name="text"/> the AI service uses the
    /// text it stored at indexing time. Throws <see cref="AiRejectedException"/> for not-enough-content or quota.
    /// </summary>
    Task<AiSummary> SummarizeAsync(Guid userId, Guid? resourceId, string? title, string? description, string? text, CancellationToken ct);

    Task<IReadOnlyList<AiTagSuggestion>> SuggestTagsAsync(
        Guid userId, Guid? resourceId, string? title, string? description, string? text,
        IReadOnlyCollection<string> existingTags, CancellationToken ct);

    Task<AiUsageInfo> GetUsageAsync(Guid userId, CancellationToken ct);

    /// <summary>Hybrid search over the user's chunks: at most one hit per resource, best first.</summary>
    Task<IReadOnlyList<AiSearchHit>> SearchAsync(Guid userId, string query, int limit, CancellationToken ct);

    /// <summary>Resources nearest to this one by embedding, best first, never the resource itself.</summary>
    Task<IReadOnlyList<AiRelatedHit>> RelatedAsync(Guid userId, Guid resourceId, int limit, CancellationToken ct);

    // ---- chat (Doc 02 §5.2). Sessions live in the AI service; a missing or foreign session is a NotFound rejection. ----

    Task<AiChatSession> CreateChatSessionAsync(Guid userId, Guid? scopeCollectionId, CancellationToken ct);
    Task<IReadOnlyList<AiChatSession>> ListChatSessionsAsync(Guid userId, CancellationToken ct);
    Task<AiChatDetail> GetChatSessionAsync(Guid userId, Guid sessionId, CancellationToken ct);
    Task<AiChatSession> RenameChatSessionAsync(Guid userId, Guid sessionId, string title, CancellationToken ct);
    Task DeleteChatSessionAsync(Guid userId, Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Asks a question. Problems that come before the answer starts (no such chat, over the quota, service down) are
    /// thrown here; once it returns, the answer is read as events and a failure part-way is an <c>error</c> event.
    /// </summary>
    Task<IAiChatAnswer> OpenChatAsync(Guid userId, Guid sessionId, string content, CancellationToken ct);

    /// <summary>Never throws: an AI service that does not answer is reported as not available.</summary>
    Task<AiStatusInfo> GetStatusAsync(CancellationToken ct);
}

public sealed record AiSummary(string Summary, string Source, string Model);
public sealed record AiChatSession(Guid Id, string Title, Guid? ScopeCollectionId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record AiChatCitation(int N, Guid ResourceId, string? Title, string? Quote);
public sealed record AiChatMessage(Guid Id, string Role, string Content, IReadOnlyList<AiChatCitation> Citations, DateTimeOffset CreatedAt);
public sealed record AiChatDetail(AiChatSession Session, IReadOnlyList<AiChatMessage> Messages);

/// <summary>One server-sent event from the AI service: <c>token</c>, <c>citations</c>, <c>done</c> or <c>error</c>.</summary>
public sealed record AiChatEvent(string Name, System.Text.Json.JsonElement Data);

/// <summary>An answer being read. Disposing it closes the connection to the AI service.</summary>
public interface IAiChatAnswer : IAsyncDisposable
{
    IAsyncEnumerable<AiChatEvent> ReadAsync(CancellationToken ct);
}

public sealed record AiSearchHit(Guid ResourceId, string Snippet, double Score);
public sealed record AiRelatedHit(Guid ResourceId, double Score);
public sealed record AiTagSuggestion(string Name, bool Existing);
public sealed record AiUsageItem(string Operation, int Used, int? Limit);
public sealed record AiUsageInfo(DateTimeOffset ResetAt, IReadOnlyList<AiUsageItem> Items);
public sealed record AiStatusInfo(bool Available, string Profile, bool ChatAvailable);

/// <summary>The AI service understood the request and said no: too little text, or the daily limit.</summary>
public sealed class AiRejectedException(ProblemCode code, DateTimeOffset? resetAt = null, int? retryAfterSeconds = null)
    : Exception($"The AI service refused the request: {ProblemCatalogue.Code(code)}.")
{
    public ProblemCode Code { get; } = code;
    public DateTimeOffset? ResetAt { get; } = resetAt;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>The AI service did not answer, or the circuit is open: map to <c>503 ai_unavailable</c> (NFR-REL-04).</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
