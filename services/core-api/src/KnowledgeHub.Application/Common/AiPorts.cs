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

    /// <summary>Never throws: an AI service that does not answer is reported as not available.</summary>
    Task<AiStatusInfo> GetStatusAsync(CancellationToken ct);
}

public sealed record AiSummary(string Summary, string Source, string Model);
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
