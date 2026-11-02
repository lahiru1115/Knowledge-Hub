using System.Globalization;
using System.Text;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Library;

/// <summary>Collections (FR-COL-01 to 05). Every query starts from <c>OwnedBy(userId)</c>; a foreign ID is a 404.</summary>
public sealed class CollectionService(IAppDbContext db, IClock clock)
{
    public async Task<CollectionListDto> ListAsync(Guid userId, CancellationToken ct)
    {
        var counts = await db.Resources.OwnedBy(userId)
            .GroupBy(r => r.CollectionId)
            .Select(g => new { CollectionId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byId = counts.Where(c => c.CollectionId is not null).ToDictionary(c => c.CollectionId!.Value, c => c.Count);
        var inbox = counts.FirstOrDefault(c => c.CollectionId is null)?.Count ?? 0;

        var collections = await db.Collections.OwnedBy(userId).AsNoTracking()
            .OrderByDescending(c => c.IsPinned).ThenBy(c => c.Position).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync(ct);
        return new CollectionListDto([.. collections.Select(c => ToDto(c, byId.GetValueOrDefault(c.Id)))], inbox);
    }

    public async Task<CollectionDto> GetAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var c = await db.Collections.OwnedBy(userId).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        return ToDto(c, await CountAsync(userId, id, ct));
    }

    public async Task<CollectionDto> CreateAsync(Guid userId, CollectionCreate input, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var name = ValidateName(input.Name, errors);
        var description = ValidateDescription(input.Description, errors);
        var color = ValidateColor(input.Color, errors);
        var icon = ValidateIcon(input.Icon, errors);
        errors.ThrowIfAny();
        await EnsureNameFreeAsync(userId, name!, exceptId: null, ct);

        var now = clock.UtcNow;
        var position = await db.Collections.OwnedBy(userId).MaxAsync(c => (int?)c.Position, ct) ?? -1;
        var collection = new Collection
        {
            UserId = userId, Name = name!, Description = description, Color = color, Icon = icon,
            Position = position + 1, CreatedAt = now, UpdatedAt = now,
        };
        db.Collections.Add(collection);
        await SaveAsync(ct);
        return ToDto(collection, 0);
    }

    public async Task<CollectionDto> UpdateAsync(Guid userId, Guid id, CollectionUpdate input, CancellationToken ct)
    {
        var c = await db.Collections.OwnedBy(userId).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        if (input.ExpectedUpdatedAt is { } expected && !Same(expected, c.UpdatedAt))
            throw new AppException(ProblemCode.EditConflict);

        var errors = new FieldErrors();
        var name = input.Name is null ? null : ValidateName(input.Name, errors);
        var description = input.Description.IsSet ? ValidateDescription(input.Description.Value, errors) : null;
        var color = input.Color.IsSet ? ValidateColor(input.Color.Value, errors) : null;
        var icon = input.Icon.IsSet ? ValidateIcon(input.Icon.Value, errors) : null;
        errors.ThrowIfAny();

        if (name is not null && name != c.Name)
        {
            await EnsureNameFreeAsync(userId, name, exceptId: id, ct);
            c.Name = name;
        }

        if (input.Description.IsSet) c.Description = description;
        if (input.Color.IsSet) c.Color = color;
        if (input.Icon.IsSet) c.Icon = icon;
        if (input.IsPinned is { } pinned) c.IsPinned = pinned;
        c.UpdatedAt = clock.UtcNow;
        await SaveAsync(ct);
        return ToDto(c, await CountAsync(userId, id, ct));
    }

    /// <summary>Saves the sidebar order: the listed collections get positions 0, 1, 2 … in the order given (FR-COL-03).</summary>
    public async Task ReorderAsync(Guid userId, ReorderRequest input, CancellationToken ct)
    {
        if (input.Ids is null) throw AppException.Field("ids", "Send the list of collection IDs in the order you want.");
        var ids = input.Ids.Distinct().ToList();
        var owned = await db.Collections.OwnedBy(userId).Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        if (owned.Count != ids.Count) throw new AppException(ProblemCode.NotFound);

        var byId = owned.ToDictionary(c => c.Id);
        for (var i = 0; i < ids.Count; i++) byId[ids[i]].Position = i;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes a collection; the caller chooses what happens to its resources (FR-COL-05, DR-04).</summary>
    public async Task DeleteAsync(Guid userId, Guid id, DeleteCollectionMode? mode, Guid? targetId, CancellationToken ct)
    {
        if (mode is null) throw AppException.Field("resources", "Choose delete or move.");
        var collection = await db.Collections.OwnedBy(userId).FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new AppException(ProblemCode.NotFound);
        if (mode == DeleteCollectionMode.Move && targetId is { } t)
        {
            if (t == id) throw AppException.Field("target_collection_id", "Choose a different collection.");
            if (!await db.Collections.OwnedBy(userId).AnyAsync(c => c.Id == t, ct)) throw new AppException(ProblemCode.NotFound);
        }

        var now = clock.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Includes resources already in the trash, so the foreign key never blocks the delete. Those go to the
        // Inbox, which is also where they land if restored.
        var resources = await db.Resources.IgnoreQueryFilters().OwnedBy(userId).Where(r => r.CollectionId == id).ToListAsync(ct);
        foreach (var r in resources)
        {
            if (r.DeletedAt is not null) r.CollectionId = null;
            else if (mode == DeleteCollectionMode.Delete) { r.DeletedAt = now; r.CollectionId = null; }
            else r.CollectionId = targetId;
            r.UpdatedAt = now;
        }

        db.Collections.Remove(collection);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    // ---- helpers ----

    private Task<int> CountAsync(Guid userId, Guid id, CancellationToken ct) =>
        db.Resources.OwnedBy(userId).CountAsync(r => r.CollectionId == id, ct);

    private static CollectionDto ToDto(Collection c, int count) =>
        new(c.Id, c.Name, c.Description, c.Color, c.Icon, c.IsPinned, c.Position, count, c.CreatedAt, c.UpdatedAt);

    private async Task EnsureNameFreeAsync(Guid userId, string name, Guid? exceptId, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
        // EF translates ToLower() to lower(name), the same expression as the unique index (neither StringComparison
        // nor ToLowerInvariant() translates). The database lower() is what the index uses, so use it here too.
#pragma warning disable CA1862, CA1304, CA1311
        var taken = await db.Collections.OwnedBy(userId).AnyAsync(c => c.Name.ToLower() == lower && c.Id != exceptId, ct);
#pragma warning restore CA1862, CA1304, CA1311
        if (taken)
            throw new AppException(ProblemCode.CollectionNameTaken);
    }

    /// <summary>The unique index also guards against two requests racing past the check above.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex)) { throw new AppException(ProblemCode.CollectionNameTaken); }
    }

    private static bool Same(DateTimeOffset a, DateTimeOffset b) => Math.Abs((a - b).TotalMilliseconds) < 1;


    private static string? ValidateName(string? input, FieldErrors errors)
    {
        var name = input?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) { errors.Add("name", "Must be 1 to 100 characters."); return null; }
        return name;
    }

    private static string? ValidateDescription(string? input, FieldErrors errors)
    {
        var d = input?.Trim();
        if (string.IsNullOrEmpty(d)) return null;
        if (d.Length > 500) { errors.Add("description", "Must be 500 characters or fewer."); return null; }
        return d;
    }

    private static string? ValidateColor(string? input, FieldErrors errors)
    {
        if (string.IsNullOrEmpty(input)) return null;
        if (!CollectionColors.All.Contains(input)) errors.Add("color", $"Must be one of: {string.Join(", ", CollectionColors.All)}.");
        return input;
    }

    /// <summary>One emoji: a single visible character that is not a letter, digit or punctuation mark.</summary>
    private static string? ValidateIcon(string? input, FieldErrors errors)
    {
        if (string.IsNullOrEmpty(input)) return null;
        var single = new StringInfo(input).LengthInTextElements == 1;
        var first = Rune.GetRuneAt(input, 0);
        if (!single || Rune.IsLetterOrDigit(first) || Rune.IsPunctuation(first) || Rune.IsWhiteSpace(first))
            errors.Add("icon", "Must be a single emoji.");
        return input;
    }
}
