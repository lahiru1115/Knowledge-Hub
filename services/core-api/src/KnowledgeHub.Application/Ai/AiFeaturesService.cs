using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Ai;

public sealed record TagSuggestionItemDto(string Name, bool Existing);
public sealed record TagSuggestionsDto(IReadOnlyList<TagSuggestionItemDto> Items, bool AiAvailable);
public sealed record TagSuggestionRequest(string? Url, Guid? ResourceId);
public sealed record AiUsageItemDto(string Operation, int Used, int? Limit);
public sealed record AiUsageDto(DateTimeOffset ResetAt, IReadOnlyList<AiUsageItemDto> Items);
public sealed record AiStatusDto(bool Available, string Profile, bool ChatAvailable);

/// <summary>
/// What users can ask of the AI service (Doc 02 §5.2). It owns the mapping from AI failures to the API's errors:
/// the AI service being down is <c>503 ai_unavailable</c>, a refusal keeps its own code (FR-AI-09, FR-AI-10).
/// </summary>
public sealed class AiFeaturesService(
    IAppDbContext db, IClock clock, IAiClient ai, IMetadataFetcher fetcher, ResourceService resources)
{
    /// <summary>Retry-After for an AI service that is not answering; the circuit breaker waits as long.</summary>
    public const int UnavailableRetrySeconds = 30;

    /// <summary>The page excerpt sent for a URL that is not saved yet.</summary>
    private const int ExcerptChars = 2000;

    private const int MaxExistingTags = 500;

    public async Task<ResourceDto> SummarizeAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var r = await db.Resources.OwnedBy(userId).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        var summary = await CallAsync(() => ai.SummarizeAsync(userId, r.Id, r.Title, r.Description, null, ct));
        r.Summary = summary.Summary;
        r.SummarySource = summary.Source == "ai" ? SummarySource.Ai : SummarySource.Extracted;
        r.UpdatedAt = clock.UtcNow;   // the user asked for this, so it is an edit like any other
        await db.SaveChangesAsync(ct);
        return await resources.GetAsync(userId, id, ct);
    }

    public async Task RemoveSummaryAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var r = await db.Resources.OwnedBy(userId).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        r.Summary = null;
        r.SummarySource = SummarySource.None;
        r.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<TagSuggestionsDto> SuggestTagsAsync(Guid userId, TagSuggestionRequest request, CancellationToken ct)
    {
        if ((request.Url is null) == (request.ResourceId is null))
            throw AppException.Field("url", "Send either a url or a resource_id.");

        Guid? resourceId = null;
        string? title, description, text = null;
        if (request.ResourceId is { } rid)
        {
            var r = await db.Resources.OwnedBy(userId).AsNoTracking().FirstOrDefaultAsync(x => x.Id == rid, ct)
                ?? throw new AppException(ProblemCode.NotFound);
            (resourceId, title, description) = (r.Id, r.Title, r.Description);
        }
        else
        {
            if (!UrlNormalizer.IsValid(request.Url))
                throw AppException.Field("url", "Enter a link that starts with http:// or https://.");
            // Same guarded fetcher as the preview, so this cannot be used to reach the private network.
            var fetched = await fetcher.FetchAsync(request.Url!.Trim(), ct);
            if (fetched.Metadata is not { } m) return new TagSuggestionsDto([], AiAvailable: true);
            (title, description) = (m.Title, m.Description);
            text = m.Text is { Length: > ExcerptChars } ? m.Text[..ExcerptChars] : m.Text;
        }

        var existing = await db.Tags.OwnedBy(userId).AsNoTracking()
            .OrderByDescending(t => db.ResourceTags.Count(rt => rt.TagId == t.Id)).ThenBy(t => t.Name)
            .Select(t => t.Name).Take(MaxExistingTags).ToListAsync(ct);
        try
        {
            var items = await CallAsync(() => ai.SuggestTagsAsync(userId, resourceId, title, description, text, existing, ct));
            return new TagSuggestionsDto([.. items.Select(i => new TagSuggestionItemDto(i.Name, i.Existing))], AiAvailable: true);
        }
        catch (AppException e) when (e.Code == ProblemCode.AiUnavailable)
        {
            // Suggestions are a convenience: saving a link must not depend on them (FR-AI-09).
            return new TagSuggestionsDto([], AiAvailable: false);
        }
    }

    public async Task<AiUsageDto> UsageAsync(Guid userId, CancellationToken ct)
    {
        var u = await CallAsync(() => ai.GetUsageAsync(userId, ct));
        return new AiUsageDto(u.ResetAt, [.. u.Items.Select(i => new AiUsageItemDto(i.Operation, i.Used, i.Limit))]);
    }

    public async Task<AiStatusDto> StatusAsync(CancellationToken ct)
    {
        var s = await ai.GetStatusAsync(ct);
        return new AiStatusDto(s.Available, s.Profile, s.ChatAvailable);
    }

    /// <summary>Runs an AI call and turns its failures into the API's problems.</summary>
    internal static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (AiUnavailableException)
        {
            throw Unavailable();
        }
        catch (AiRejectedException e)
        {
            var ex = new AppException(e.Code);
            if (e.ResetAt is { } reset) ex.Extensions["reset_at"] = reset;
            if (e.RetryAfterSeconds is { } wait) ex.Extensions["retry_after"] = wait;
            throw ex;
        }
    }

    private static AppException Unavailable()
    {
        var ex = new AppException(ProblemCode.AiUnavailable);
        ex.Extensions["retry_after"] = UnavailableRetrySeconds;
        return ex;
    }
}
