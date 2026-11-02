using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Library;

/// <summary>Turns tag names from a request into the user's tags, creating the missing ones (FR-TAG-01, FR-RES-07).</summary>
internal sealed class TagResolver(IAppDbContext db)
{
    /// <summary>Normalises names, drops duplicates, and reports bad names and the 20-per-resource limit as field errors.</summary>
    public static List<string> Normalize(IReadOnlyList<string>? names, string field, FieldErrors errors)
    {
        var result = new List<string>();
        foreach (var raw in names ?? [])
        {
            if (!TagNames.TryNormalize(raw, out var name))
            {
                errors.Add(field, $"'{raw}' is not a valid tag. Use 1 to {TagNames.MaxLength} letters, digits, spaces or - _ . # +.");
                continue;
            }

            if (!result.Contains(name)) result.Add(name);
        }

        if (result.Count > TagNames.MaxPerResource) errors.Add(field, $"Use at most {TagNames.MaxPerResource} tags.");
        return result;
    }

    /// <summary>Existing tags by name, plus new tags added to the context for the missing names.</summary>
    public async Task<Dictionary<string, Tag>> EnsureAsync(Guid userId, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        var existing = await db.Tags.OwnedBy(userId).Where(t => names.Contains(t.Name)).ToListAsync(ct);
        var map = existing.ToDictionary(t => t.Name);
        foreach (var name in names.Where(n => !map.ContainsKey(n)))
        {
            var tag = new Tag { UserId = userId, Name = name };
            db.Tags.Add(tag);
            map[name] = tag;
        }

        return map;
    }

    public static string TagsText(IEnumerable<string> names) => string.Join(' ', names.Order(StringComparer.Ordinal));
}
