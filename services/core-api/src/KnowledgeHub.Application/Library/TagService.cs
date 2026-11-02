using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Library;

/// <summary>Tags (FR-TAG-01 to 05).</summary>
public sealed class TagService(IAppDbContext db, ITagsTextSync tagsText)
{
    public async Task<TagListDto> ListAsync(Guid userId, CancellationToken ct)
    {
        // Counts only resources that are not in the trash (the soft-delete filter applies to ResourceTags too).
        var tags = await db.Tags.OwnedBy(userId).AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new TagDto(t.Id, t.Name, db.ResourceTags.Count(rt => rt.TagId == t.Id)))
            .ToListAsync(ct);
        return new TagListDto(tags);
    }

    public async Task<TagDto> RenameAsync(Guid userId, Guid id, RenameTagRequest input, CancellationToken ct)
    {
        var tag = await db.Tags.OwnedBy(userId).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new AppException(ProblemCode.NotFound);
        if (!TagNames.TryNormalize(input.Name, out var name))
            throw AppException.Field("name", $"Use 1 to {TagNames.MaxLength} letters, digits, spaces or - _ . # +.");

        if (name != tag.Name)
        {
            var other = await db.Tags.OwnedBy(userId).FirstOrDefaultAsync(t => t.Name == name && t.Id != id, ct);
            if (other is not null)
            {
                // The client offers to merge the two instead (FR-TAG-03).
                var conflict = new AppException(ProblemCode.TagNameTaken);
                conflict.Extensions["suggest_merge_tag_id"] = other.Id;
                throw conflict;
            }

            tag.Name = name;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex)) { throw new AppException(ProblemCode.TagNameTaken); }
            await tagsText.RefreshAsync(userId, await ResourceIdsAsync(id, ct), ct);
        }

        return await DtoAsync(userId, id, ct);
    }

    /// <summary>Merging A into B gives every resource with A the tag B (no duplicates) and deletes A (FR-TAG-04).</summary>
    public async Task<TagDto> MergeAsync(Guid userId, Guid id, MergeTagRequest input, CancellationToken ct)
    {
        if (input.TargetTagId is not { } targetId) throw AppException.Field("target_tag_id", "Choose the tag to merge into.");
        if (targetId == id) throw AppException.Field("target_tag_id", "Choose a different tag.");
        var source = await db.Tags.OwnedBy(userId).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new AppException(ProblemCode.NotFound);
        var target = await db.Tags.OwnedBy(userId).FirstOrDefaultAsync(t => t.Id == targetId, ct) ?? throw new AppException(ProblemCode.NotFound);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Includes resources in the trash: they keep their tags if restored.
        var links = await db.ResourceTags.IgnoreQueryFilters().Where(rt => rt.TagId == id).ToListAsync(ct);
        var affected = links.Select(l => l.ResourceId).ToList();
        var alreadyTarget = (await db.ResourceTags.IgnoreQueryFilters()
            .Where(rt => rt.TagId == targetId && affected.Contains(rt.ResourceId)).Select(rt => rt.ResourceId).ToListAsync(ct)).ToHashSet();
        foreach (var link in links)
        {
            if (!alreadyTarget.Contains(link.ResourceId))
                db.ResourceTags.Add(new ResourceTag { ResourceId = link.ResourceId, TagId = targetId, Tag = target, Source = link.Source });
            db.ResourceTags.Remove(link);
        }

        db.Tags.Remove(source);
        await db.SaveChangesAsync(ct);
        await tagsText.RefreshAsync(userId, affected, ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(userId, targetId, ct);
    }

    /// <summary>The tag is removed from all resources; the resources stay (FR-TAG-05).</summary>
    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var tag = await db.Tags.OwnedBy(userId).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new AppException(ProblemCode.NotFound);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var affected = await ResourceIdsAsync(id, ct);
        db.Tags.Remove(tag); // the links go with it (cascade)
        await db.SaveChangesAsync(ct);
        await tagsText.RefreshAsync(userId, affected, ct);
        await tx.CommitAsync(ct);
    }

    private async Task<List<Guid>> ResourceIdsAsync(Guid tagId, CancellationToken ct) =>
        await db.ResourceTags.IgnoreQueryFilters().Where(rt => rt.TagId == tagId).Select(rt => rt.ResourceId).ToListAsync(ct);

    private async Task<TagDto> DtoAsync(Guid userId, Guid id, CancellationToken ct) =>
        (await ListAsync(userId, ct)).Items.Single(t => t.Id == id);

}
