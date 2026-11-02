using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Application.Library;

/// <summary>
/// Events that exist for the AI service (index, remove from index, purge a user). The AI service arrives in Stage 5,
/// so for now they complete at once; the messages are already written, so Stage 5 only has to replace this handler
/// (and run a one-off re-index for resources saved before it existed).
/// </summary>
public sealed partial class AiEventPlaceholderHandler(ILogger<AiEventPlaceholderHandler> logger) : IOutboxHandler
{
    public IReadOnlyCollection<string> Types { get; } =
    [
        OutboxEventTypes.ResourceContentChanged, OutboxEventTypes.ResourceDeleted,
        OutboxEventTypes.ResourceRestored, OutboxEventTypes.UserDeleted,
    ];

    public Task HandleAsync(OutboxContext context, CancellationToken ct)
    {
        LogSkipped(context.Type);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No AI service yet; {Type} completed without indexing")]
    private partial void LogSkipped(string type);
}
