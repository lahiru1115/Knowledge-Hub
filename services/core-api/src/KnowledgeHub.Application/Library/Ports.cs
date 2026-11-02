using KnowledgeHub.Domain;

namespace KnowledgeHub.Application.Library;

/// <summary>Runs a validated list/search request (keyword search is PostgreSQL full text, Doc 02 §7.2).</summary>
public interface IResourceSearch
{
    /// <summary>Resources with their tags loaded, in the requested order, plus the total before paging.</summary>
    Task<(IReadOnlyList<Resource> Items, int Total)> SearchAsync(Guid userId, ResourceFilter filter, CancellationToken ct);
}

/// <summary>Rebuilds <c>tags_text</c>, which feeds the search index, after tags change in bulk (set-based, Doc 02 §7.2).</summary>
public interface ITagsTextSync
{
    Task RefreshAsync(Guid userId, IReadOnlyCollection<Guid> resourceIds, CancellationToken ct);
}
