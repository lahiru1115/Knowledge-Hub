using KnowledgeHub.Application.Library;
using KnowledgeHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Infrastructure.Library;

/// <summary>Set-based rebuild of <c>tags_text</c> so search sees renamed, merged, added and removed tags (Doc 02 §7.2).</summary>
public sealed class TagsTextSync(AppDbContext db) : ITagsTextSync
{
    public async Task RefreshAsync(Guid userId, IReadOnlyCollection<Guid> resourceIds, CancellationToken ct)
    {
        if (resourceIds.Count == 0) return;
        var ids = resourceIds.ToArray();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE core.resources r
            SET tags_text = COALESCE((
                SELECT string_agg(t.name, ' ' ORDER BY t.name COLLATE "C")
                FROM core.resource_tags rt JOIN core.tags t ON t.id = rt.tag_id
                WHERE rt.resource_id = r.id), '')
            WHERE r.user_id = {userId} AND r.id = ANY({ids})
            """, ct);
    }
}
