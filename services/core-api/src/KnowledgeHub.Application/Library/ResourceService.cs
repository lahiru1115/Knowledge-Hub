using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Library;

/// <summary>
/// Resources (FR-RES-01 to 12, FR-SRCH-01 to 04). A foreign or missing ID is always <c>404 not_found</c>, and every
/// ID inside a request (collection, tags) is checked for ownership too (DR-02, NFR-SEC-05).
/// </summary>
public sealed class ResourceService(IAppDbContext db, IClock clock, IResourceSearch search, ITagsTextSync tagsText, IOutbox outbox)
{
    public static readonly TimeSpan UndoWindow = TimeSpan.FromDays(7);
    public const int MaxPageSize = 100;
    public const int MaxBulk = 100;

    private readonly TagResolver _tags = new(db);

    // ---- read ----

    public async Task<ResourceDto> GetAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var r = await WithTags(db.Resources.OwnedBy(userId).AsNoTracking()).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        return ToDto(r);
    }

    public async Task<Page<ResourceDto>> ListAsync(Guid userId, ResourceListRequest request, CancellationToken ct)
    {
        var filter = BuildFilter(request);
        var (items, total) = await search.SearchAsync(userId, filter, ct);
        return new Page<ResourceDto>([.. items.Select(ToDto)], filter.PageNumber, filter.PageSize, total);
    }

    /// <summary>The user's existing resource with the same normalised URL, if any (FR-RES-11).</summary>
    public async Task<DuplicateCheckDto> CheckUrlAsync(Guid userId, string? url, CancellationToken ct)
    {
        if (!UrlNormalizer.TryNormalize(url, out var normalized))
            throw AppException.Field("url", "Enter a link that starts with http:// or https://.");
        var existing = await WithTags(db.Resources.OwnedBy(userId).AsNoTracking())
            .Where(r => r.UrlNormalized == normalized).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        return new DuplicateCheckDto(existing is null ? null : ToDto(existing));
    }

    // ---- create / update ----

    public async Task<ResourceDto> CreateAsync(Guid userId, ResourceCreate input, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var url = ValidateUrl(input.Url, errors);
        var title = ValidateText(input.Title, "title", 300, errors);
        var description = ValidateText(input.Description, "description", 2000, errors);
        var notes = ValidateText(input.Notes, "notes", 100_000, errors, trim: false);
        if (input.Type is { } t && !Enum.IsDefined(t)) errors.Add("type", "Not a valid type.");
        var tagNames = TagResolver.Normalize(input.Tags, "tags", errors);
        var aiNames = AiSubset(input.AiTags, tagNames, errors);
        errors.ThrowIfAny();
        await EnsureCollectionAsync(userId, input.CollectionId, ct);

        var now = clock.UtcNow;
        _ = UrlNormalizer.TryNormalize(url, out var normalized);
        var resource = new Resource
        {
            UserId = userId,
            Url = url!,
            UrlNormalized = normalized,
            Title = title,
            Description = description,
            Notes = notes,
            Type = input.Type ?? ResourceType.Website,
            CollectionId = input.CollectionId,
            MetadataStatus = MetadataStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            TagsText = TagResolver.TagsText(tagNames),
            // Whatever the user typed is theirs: extraction fills only the rest (FR-RES-03).
            UserEditedFields = (title is not null ? EditedFields.Title : 0)
                | (description is not null ? EditedFields.Description : 0)
                | (input.Type is not null ? EditedFields.Type : 0),
        };
        db.Resources.Add(resource);
        outbox.Add(OutboxEventTypes.ResourceCreated, new { resource_id = resource.Id, user_id = userId });
        var tags = await _tags.EnsureAsync(userId, tagNames, ct);
        foreach (var name in tagNames)
            resource.ResourceTags.Add(new ResourceTag { ResourceId = resource.Id, TagId = tags[name].Id, Tag = tags[name], Source = aiNames.Contains(name) ? TagSource.Ai : TagSource.User });
        await db.SaveChangesAsync(ct);
        return ToDto(resource);
    }

    public async Task<ResourceDto> UpdateAsync(Guid userId, Guid id, ResourceUpdate input, CancellationToken ct)
    {
        var r = await WithTags(db.Resources.OwnedBy(userId)).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        if (input.ExpectedUpdatedAt is { } expected && Math.Abs((expected - r.UpdatedAt).TotalMilliseconds) >= 1)
            throw new AppException(ProblemCode.EditConflict);

        var errors = new FieldErrors();
        var url = input.Url is null ? null : ValidateUrl(input.Url, errors);
        var title = input.Title.IsSet ? ValidateText(input.Title.Value, "title", 300, errors) : null;
        var description = input.Description.IsSet ? ValidateText(input.Description.Value, "description", 2000, errors) : null;
        var notes = input.Notes.IsSet ? ValidateText(input.Notes.Value, "notes", 100_000, errors, trim: false) : null;
        var summary = input.Summary.IsSet ? ValidateText(input.Summary.Value, "summary", 2000, errors) : null;
        if (input.Type is { } t && !Enum.IsDefined(t)) errors.Add("type", "Not a valid type.");
        var tagNames = input.Tags is null ? null : TagResolver.Normalize(input.Tags, "tags", errors);
        var aiNames = tagNames is null ? [] : AiSubset(input.AiTags, tagNames, errors);
        errors.ThrowIfAny();
        if (input.CollectionId is { IsSet: true, Value: { } collectionId }) await EnsureCollectionAsync(userId, collectionId, ct);

        var urlChanged = false;
        if (url is not null && url != r.Url)
        {
            _ = UrlNormalizer.TryNormalize(url, out var normalized);
            r.Url = url;
            r.UrlNormalized = normalized;
            r.MetadataStatus = MetadataStatus.Pending; // a new address means fetching its details again (FR-RES-03)
            r.MetadataError = null;
            urlChanged = true;
        }

        if (input.Title.IsSet) { r.Title = title; r.UserEditedFields = Flag(r.UserEditedFields, EditedFields.Title, title is not null); }
        if (input.Description.IsSet) { r.Description = description; r.UserEditedFields = Flag(r.UserEditedFields, EditedFields.Description, description is not null); }
        if (input.Notes.IsSet) r.Notes = notes;
        if (input.Type is { } type) { r.Type = type; r.UserEditedFields |= EditedFields.Type; }
        if (input.CollectionId.IsSet) r.CollectionId = input.CollectionId.Value;
        if (input.IsFavourite is { } fav) r.IsFavourite = fav;
        if (input.IsArchived is { } archived) r.IsArchived = archived;
        if (input.Summary.IsSet)
        {
            r.Summary = summary;
            r.SummarySource = summary is null ? SummarySource.None : SummarySource.User;
        }

        if (tagNames is not null) await SetTagsAsync(userId, r, tagNames, aiNames, ct);
        r.UpdatedAt = clock.UtcNow;
        // Favourite and archive do not change what is indexed; everything else might (Doc 02 §5.3).
        if (urlChanged || input.Title.IsSet || input.Description.IsSet || input.Notes.IsSet || input.Type is not null
            || input.CollectionId.IsSet || input.Tags is not null)
            outbox.Add(OutboxEventTypes.ResourceUpdated, new { resource_id = r.Id, user_id = userId, url_changed = urlChanged });
        await db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    /// <summary>Tries to fetch the page's details again, for a failed or stale resource (FR-META-03).</summary>
    public async Task<ResourceDto> RefreshMetadataAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var r = await WithTags(db.Resources.OwnedBy(userId)).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        r.MetadataStatus = MetadataStatus.Pending;
        r.MetadataError = null;
        outbox.Add(OutboxEventTypes.ResourceUpdated, new { resource_id = r.Id, user_id = userId, url_changed = false, refetch = true });
        await db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    // ---- delete / restore ----

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var r = await db.Resources.OwnedBy(userId).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        r.DeletedAt = clock.UtcNow;
        outbox.Add(OutboxEventTypes.ResourceDeleted, new { resource_id = r.Id, user_id = userId });
        await db.SaveChangesAsync(ct);
    }

    public async Task<ResourceDto> RestoreAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var r = await WithTags(db.Resources.IgnoreQueryFilters().OwnedBy(userId))
            .FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt != null && x.DeletedAt > now - UndoWindow, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        r.DeletedAt = null;
        r.UpdatedAt = now;
        outbox.Add(OutboxEventTypes.ResourceRestored, new { resource_id = r.Id, user_id = userId });
        await db.SaveChangesAsync(ct);
        return ToDto(r);
    }

    // ---- bulk (FR-RES-12) ----

    /// <summary>One action on up to 100 resources: all of them or none, and the number actually changed.</summary>
    public async Task<BulkResult> BulkAsync(Guid userId, BulkRequest input, CancellationToken ct)
    {
        var errors = new FieldErrors();
        if (input.Action is null) errors.Add("action", "Choose an action.");
        var ids = (input.Ids ?? []).Distinct().ToList();
        if (ids.Count is 0 or > MaxBulk) errors.Add("ids", $"Send between 1 and {MaxBulk} IDs.");
        if (input.Action == BulkAction.Move && !input.CollectionId.IsSet) errors.Add("collection_id", "Say where to move them (null for the Inbox).");
        var tagNames = new List<string>();
        if (input.Action is BulkAction.AddTags or BulkAction.RemoveTags)
        {
            if (input.Tags is not { Count: > 0 }) errors.Add("tags", "Send at least one tag.");
            tagNames = TagResolver.Normalize(input.Tags, "tags", errors);
        }

        errors.ThrowIfAny();
        if (input.Action == BulkAction.Move) await EnsureCollectionAsync(userId, input.CollectionId.Value, ct);

        var now = clock.UtcNow;
        var restoring = input.Action == BulkAction.Restore;
        IQueryable<Resource> source = db.Resources.OwnedBy(userId);
        if (restoring) source = db.Resources.IgnoreQueryFilters().OwnedBy(userId).Where(r => r.DeletedAt != null && r.DeletedAt > now - UndoWindow);
        var resources = await WithTags(source).Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        if (resources.Count != ids.Count) throw new AppException(ProblemCode.NotFound); // none are changed

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = new List<Resource>();
        switch (input.Action)
        {
            case BulkAction.Move:
                foreach (var r in resources.Where(r => r.CollectionId != input.CollectionId.Value)) { r.CollectionId = input.CollectionId.Value; changed.Add(r); }
                break;
            case BulkAction.Delete:
                foreach (var r in resources) { r.DeletedAt = now; changed.Add(r); }
                break;
            case BulkAction.Restore:
                foreach (var r in resources) { r.DeletedAt = null; changed.Add(r); }
                break;
            case BulkAction.AddTags:
                var tags = await _tags.EnsureAsync(userId, tagNames, ct);
                foreach (var r in resources)
                {
                    var missing = tagNames.Where(n => r.ResourceTags.All(rt => rt.Tag.Name != n)).ToList();
                    foreach (var n in missing) r.ResourceTags.Add(new ResourceTag { ResourceId = r.Id, TagId = tags[n].Id, Tag = tags[n] });
                    if (missing.Count > 0) changed.Add(r);
                }
                break;
            case BulkAction.RemoveTags:
                foreach (var r in resources)
                {
                    var links = r.ResourceTags.Where(rt => tagNames.Contains(rt.Tag.Name)).ToList();
                    foreach (var link in links) r.ResourceTags.Remove(link);
                    if (links.Count > 0) changed.Add(r);
                }
                break;
        }

        foreach (var r in changed)
        {
            r.UpdatedAt = now;
            var type = input.Action switch
            {
                BulkAction.Delete => OutboxEventTypes.ResourceDeleted,
                BulkAction.Restore => OutboxEventTypes.ResourceRestored,
                _ => OutboxEventTypes.ResourceUpdated,
            };
            outbox.Add(type, new { resource_id = r.Id, user_id = userId, url_changed = false });
        }

        await db.SaveChangesAsync(ct);
        if (input.Action is BulkAction.AddTags or BulkAction.RemoveTags && changed.Count > 0)
            await tagsText.RefreshAsync(userId, [.. changed.Select(r => r.Id)], ct);
        await tx.CommitAsync(ct);
        return new BulkResult(changed.Count);
    }

    // ---- helpers ----

    private static EditedFields Flag(EditedFields flags, EditedFields flag, bool on) => on ? flags | flag : flags & ~flag;

    private static IQueryable<Resource> WithTags(IQueryable<Resource> q) => q.Include(r => r.ResourceTags).ThenInclude(rt => rt.Tag);

    public static ResourceDto ToDto(Resource r) => new(
        r.Id, r.Url, r.Title, r.Description, r.Notes, r.Type, r.CollectionId,
        [.. r.ResourceTags.Select(rt => rt.Tag.Name).Order(StringComparer.Ordinal)],
        r.FaviconUrl, r.ImageUrl, r.SiteName, r.Summary, r.SummarySource, r.IsFavourite, r.IsArchived, r.MetadataStatus,
        r.MetadataError, r.CreatedAt, r.UpdatedAt);

    private async Task SetTagsAsync(Guid userId, Resource r, List<string> desired, HashSet<string> aiNames, CancellationToken ct)
    {
        foreach (var link in r.ResourceTags.Where(rt => !desired.Contains(rt.Tag.Name)).ToList()) r.ResourceTags.Remove(link);
        var have = r.ResourceTags.Select(rt => rt.Tag.Name).ToHashSet();
        var tags = await _tags.EnsureAsync(userId, desired, ct);
        foreach (var name in desired.Where(n => !have.Contains(n)))
            r.ResourceTags.Add(new ResourceTag { ResourceId = r.Id, TagId = tags[name].Id, Tag = tags[name], Source = aiNames.Contains(name) ? TagSource.Ai : TagSource.User });
        r.TagsText = TagResolver.TagsText(desired);
    }

    private async Task EnsureCollectionAsync(Guid userId, Guid? collectionId, CancellationToken ct)
    {
        if (collectionId is null) return;
        if (!await db.Collections.OwnedBy(userId).AnyAsync(c => c.Id == collectionId, ct))
            throw new AppException(ProblemCode.NotFound);
    }

    private static HashSet<string> AiSubset(IReadOnlyList<string>? ai, List<string> tags, FieldErrors errors)
    {
        var names = TagResolver.Normalize(ai, "ai_tags", errors);
        if (names.Any(n => !tags.Contains(n))) errors.Add("ai_tags", "Every AI tag must also be in tags.");
        return [.. names];
    }

    private static string? ValidateUrl(string? url, FieldErrors errors)
    {
        if (UrlNormalizer.IsValid(url)) return url!.Trim();
        errors.Add("url", "Enter a link that starts with http:// or https:// (up to 2,048 characters).");
        return null;
    }

    private static string? ValidateText(string? value, string field, int max, FieldErrors errors, bool trim = true)
    {
        var text = trim ? value?.Trim() : value;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > max) { errors.Add(field, $"Must be {max:N0} characters or fewer."); return null; }
        return text;
    }


    /// <summary>Checks the raw query values and turns them into a filter the search can run.</summary>
    private static ResourceFilter BuildFilter(ResourceListRequest r)
    {
        var errors = new FieldErrors();
        var types = new List<ResourceType>();
        foreach (var raw in r.Type ?? [])
        {
            if (Enum.TryParse<ResourceType>(raw, ignoreCase: true, out var t) && Enum.IsDefined(t) && !char.IsDigit(raw[0])) types.Add(t);
            else errors.Add("type", $"'{raw}' is not a resource type.");
        }

        var tags = new List<string>();
        foreach (var raw in r.Tag ?? [])
        {
            if (TagNames.TryNormalize(raw, out var name)) tags.Add(name);
            else errors.Add("tag", $"'{raw}' is not a valid tag.");
        }

        Guid? collectionId = null;
        var inbox = false;
        if (!string.IsNullOrEmpty(r.CollectionId))
        {
            if (r.CollectionId == "inbox") inbox = true;
            else if (Guid.TryParse(r.CollectionId, out var g)) collectionId = g;
            else errors.Add("collection_id", "Use a collection ID or 'inbox'.");
        }

        var query = r.Q?.Trim();
        var hasQuery = !string.IsNullOrEmpty(query);
        ResourceSort sort;
        if (string.IsNullOrEmpty(r.Sort)) sort = hasQuery ? ResourceSort.Relevance : ResourceSort.Newest;
        else if (!Enum.TryParse(r.Sort, ignoreCase: true, out sort) || !Enum.IsDefined(sort) || char.IsDigit(r.Sort[0]))
        {
            errors.Add("sort", "Use relevance, newest, oldest, title or updated.");
            sort = ResourceSort.Newest;
        }

        if (sort == ResourceSort.Relevance && !hasQuery) sort = ResourceSort.Newest; // nothing to rank by
        var page = r.Page ?? 1;
        var pageSize = r.PageSize ?? 20;
        if (page < 1) errors.Add("page", "Must be 1 or more.");
        if (pageSize is < 1 or > MaxPageSize) errors.Add("page_size", $"Must be between 1 and {MaxPageSize}.");
        errors.ThrowIfAny();

        // A collection from another user simply matches nothing; the list never says whether it exists.
        return new ResourceFilter(query, types, tags, collectionId, inbox, r.Favourite, r.Archived ?? false,
            r.CreatedFrom, r.CreatedTo, sort, page, pageSize);
    }
}
