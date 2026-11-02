using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KnowledgeHub.Application.Library;

/// <summary>
/// Fetches a saved link's details in the background (FR-RES-03, FR-META-01..03). Runs for a new resource, and for an
/// update that changed the URL or asked for a retry. It only acts while the resource is <c>pending</c>, so a retried or
/// repeated message does the work once. It does not touch <c>updated_at</c>: a background job must not make the user's
/// next edit look like a conflict.
/// </summary>
public sealed partial class MetadataJobHandler(
    IAppDbContext db, IMetadataFetcher fetcher, IOutbox outbox, ILogger<MetadataJobHandler> logger) : IOutboxHandler
{
    /// <summary>Text sent to the AI service is capped so one huge page cannot bloat the outbox.</summary>
    public const int MaxTextChars = 100_000;

    public IReadOnlyCollection<string> Types { get; } = [OutboxEventTypes.ResourceCreated, OutboxEventTypes.ResourceUpdated];

    public async Task HandleAsync(OutboxContext context, CancellationToken ct)
    {
        var root = context.Payload;
        if (!root.TryGetProperty("resource_id", out var idProp) || !idProp.TryGetGuid(out var id)) return;

        var r = await db.Resources.FirstOrDefaultAsync(x => x.Id == id, ct);   // soft-deleted rows are filtered out
        if (r is null || r.MetadataStatus != MetadataStatus.Pending) return;

        var result = await fetcher.FetchAsync(r.Url, ct);

        if (result.Error is { } error)
        {
            if (result.Transient && !context.IsFinalAttempt)
                throw new MetadataFetchException(error);
            r.MetadataStatus = MetadataStatus.Failed;
            r.MetadataError = error;
            await db.SaveChangesAsync(ct);
            LogFailed(id, error);
            return;
        }

        var m = result.Metadata!;
        var edited = r.UserEditedFields;
        if (!edited.HasFlag(EditedFields.Title) && !string.IsNullOrWhiteSpace(m.Title)) r.Title = Clip(m.Title, 300);
        if (!edited.HasFlag(EditedFields.Description) && !string.IsNullOrWhiteSpace(m.Description)) r.Description = Clip(m.Description, 2000);
        if (!edited.HasFlag(EditedFields.Type)) r.Type = m.Type;
        r.ImageUrl = Clip(m.ImageUrl, 2048);
        r.FaviconUrl = Clip(m.FaviconUrl, 2048);
        r.SiteName = Clip(m.SiteName, 200);
        r.MetadataStatus = MetadataStatus.Ready;
        r.MetadataError = null;

        var hash = HashOf(m.Text);
        if (hash != r.ContentHash)
        {
            r.ContentHash = hash;
            if (m.Text is { Length: > 0 })
                outbox.Add(OutboxEventTypes.ResourceContentChanged, new
                {
                    resource_id = r.Id,
                    user_id = r.UserId,
                    title = r.Title,
                    text = m.Text.Length > MaxTextChars ? m.Text[..MaxTextChars] : m.Text,
                });
        }
        await db.SaveChangesAsync(ct);
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private static string? HashOf(string? text) =>
        string.IsNullOrEmpty(text) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [LoggerMessage(Level = LogLevel.Information, Message = "Metadata for resource {ResourceId} failed: {Error}")]
    private partial void LogFailed(Guid resourceId, MetadataError error);
}

/// <summary>A fetch failed in a way worth retrying, so the outbox should try again later.</summary>
public sealed class MetadataFetchException(MetadataError error) : Exception($"Metadata fetch failed: {error}")
{
    public MetadataError Error { get; } = error;
}
