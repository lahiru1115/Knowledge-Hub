using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Library;

/// <summary>
/// Keeps the AI service's index in step with the library (Doc 02 §5.3). Each call is idempotent, and when the AI
/// service is down the call throws, so the message waits in the outbox and is retried (FR-AI-09).
/// </summary>
public sealed class AiIndexHandler(IAppDbContext db, IAiClient ai) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } =
    [
        OutboxEventTypes.ResourceCreated, OutboxEventTypes.ResourceUpdated, OutboxEventTypes.ResourceContentChanged,
        OutboxEventTypes.ResourceRestored, OutboxEventTypes.ResourceDeleted, OutboxEventTypes.ResourcePurged,
        OutboxEventTypes.UserDeleted,
    ];

    public async Task HandleAsync(OutboxContext context, CancellationToken ct)
    {
        var payload = context.Payload;
        if (!payload.TryGetProperty("user_id", out var u) || !u.TryGetGuid(out var userId)) return;

        if (context.Type == OutboxEventTypes.UserDeleted)
        {
            await ai.PurgeUserAsync(userId, ct);
            return;
        }
        if (!payload.TryGetProperty("resource_id", out var r) || !r.TryGetGuid(out var resourceId)) return;

        switch (context.Type)
        {
            case OutboxEventTypes.ResourceDeleted:
                await ai.DeleteIndexAsync(userId, resourceId, forget: false, ct);
                return;
            case OutboxEventTypes.ResourcePurged:
                await ai.DeleteIndexAsync(userId, resourceId, forget: true, ct);
                return;
        }

        // Read the resource as it is now, not as it was when the event was written: the index should match the latest.
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == resourceId && x.UserId == userId, ct);
        if (resource is null) return;   // deleted since (the delete event follows) or never existed

        // Only a content change carries the page text; other events re-index from the text the AI service already holds.
        var text = context.Type == OutboxEventTypes.ResourceContentChanged && payload.TryGetProperty("text", out var t)
            ? t.GetString()
            : null;
        await ai.IndexAsync(userId, resourceId,
            new AiIndexRequest(resource.Title, resource.Description, resource.Notes, resource.CollectionId, text), ct);
    }
}
