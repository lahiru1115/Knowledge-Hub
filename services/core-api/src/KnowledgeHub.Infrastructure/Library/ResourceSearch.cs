using System.Text.RegularExpressions;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace KnowledgeHub.Infrastructure.Library;

/// <summary>
/// Lists and searches resources with PostgreSQL full text: a weighted <c>tsvector</c> (title A, tags B,
/// description C, notes and URL D), English stemming, a prefix match on the last word so results appear while
/// typing, and <c>ts_rank_cd</c> for relevance (FR-SRCH-01 to 04, Doc 02 §7.2).
/// </summary>
public sealed partial class ResourceSearch(AppDbContext db) : IResourceSearch
{
    private const int MaxWords = 8;

    public async Task<(IReadOnlyList<Resource> Items, int Total)> SearchAsync(Guid userId, ResourceFilter filter, CancellationToken ct)
    {
        IQueryable<Resource> q = db.Resources.AsNoTracking().OwnedBy(userId);

        q = filter.Archived ? q.Where(r => r.IsArchived) : q.Where(r => !r.IsArchived);
        if (filter.Types.Count > 0) q = q.Where(r => filter.Types.Contains(r.Type));
        // All the given tags must match.
        foreach (var tag in filter.Tags) q = q.Where(r => r.ResourceTags.Any(rt => rt.Tag.Name == tag));
        if (filter.Inbox) q = q.Where(r => r.CollectionId == null);
        else if (filter.CollectionId is { } collectionId) q = q.Where(r => r.CollectionId == collectionId);
        if (filter.Favourite is { } fav) q = q.Where(r => r.IsFavourite == fav);
        if (filter.CreatedFrom is { } from) q = q.Where(r => r.CreatedAt >= from);
        if (filter.CreatedTo is { } to) q = q.Where(r => r.CreatedAt <= to);

        var tsQuery = BuildTsQuery(filter.Query);
        if (tsQuery is not null)
            q = q.Where(r => EF.Property<NpgsqlTsVector>(r, "SearchVector").Matches(EF.Functions.ToTsQuery("english", tsQuery)));

        var total = await q.CountAsync(ct);

        IOrderedQueryable<Resource> ordered = filter.Sort switch
        {
            ResourceSort.Relevance when tsQuery is not null => q
                .OrderByDescending(r => EF.Property<NpgsqlTsVector>(r, "SearchVector").RankCoverDensity(EF.Functions.ToTsQuery("english", tsQuery)))
                .ThenByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            ResourceSort.Oldest => q.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id),
#pragma warning disable CA1304, CA1311 // ToLower() translates to the database's lower(); the invariant form does not translate
            ResourceSort.Title => q.OrderBy(r => (r.Title ?? r.Url).ToLower()).ThenBy(r => r.Id),
#pragma warning restore CA1304, CA1311
            ResourceSort.Updated => q.OrderByDescending(r => r.UpdatedAt).ThenBy(r => r.Id),
            _ => q.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
        };

        var items = await ordered
            .Skip((filter.PageNumber - 1) * filter.PageSize).Take(filter.PageSize)
            .Include(r => r.ResourceTags).ThenInclude(rt => rt.Tag)
            .AsSplitQuery()
            .ToListAsync(ct);
        return (items, total);
    }

    /// <summary>
    /// Turns what the user typed into a <c>tsquery</c>: each word must match, and the last one also matches as a
    /// prefix (<c>"conn pool"</c> becomes <c>conn &amp; pool:*</c>). Only letters and digits survive, so the text
    /// cannot inject query syntax. Returns null for an empty query, which lists everything.
    /// </summary>
    internal static string? BuildTsQuery(string? input)
    {
        var words = Words().Matches(input ?? "").Select(m => m.Value.ToLowerInvariant()).Take(MaxWords).ToList();
        if (words.Count == 0) return null;
        words[^1] += ":*";
        return string.Join(" & ", words);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
