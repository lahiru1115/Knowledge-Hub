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
}

/// <summary>The AI service did not answer, or the circuit is open: map to <c>503 ai_unavailable</c> (NFR-REL-04).</summary>
public sealed class AiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
