using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;

namespace KnowledgeHub.Application.Library;

// ---- collections ----
public sealed record CollectionDto(
    Guid Id, string Name, string? Description, string? Color, string? Icon, bool IsPinned, int Position,
    int ResourceCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CollectionListDto(IReadOnlyList<CollectionDto> Items, int InboxCount);

public sealed record CollectionCreate(string Name, string? Description, string? Color, string? Icon);

public sealed record CollectionUpdate(
    string? Name, Patch<string?> Description, Patch<string?> Color, Patch<string?> Icon,
    bool? IsPinned, DateTimeOffset? ExpectedUpdatedAt);

public sealed record ReorderRequest(IReadOnlyList<Guid>? Ids);

public enum DeleteCollectionMode { Delete, Move }

// ---- resources ----
public sealed record ResourceDto(
    Guid Id, string Url, string? Title, string? Description, string? Notes, ResourceType Type, Guid? CollectionId,
    IReadOnlyList<string> Tags, string? FaviconUrl, string? ImageUrl, string? SiteName, string? Summary,
    SummarySource SummarySource, bool IsFavourite, bool IsArchived, MetadataStatus MetadataStatus,
    MetadataError? MetadataError, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ResourceCreate(
    string? Url, string? Title, string? Description, string? Notes, ResourceType? Type, Guid? CollectionId,
    IReadOnlyList<string>? Tags, IReadOnlyList<string>? AiTags);

/// <summary>PATCH body. Fields that can be cleared use <see cref="Patch{T}"/>; the rest are null when absent.</summary>
public sealed record ResourceUpdate(
    string? Url, Patch<string?> Title, Patch<string?> Description, Patch<string?> Notes, ResourceType? Type,
    Patch<Guid?> CollectionId, IReadOnlyList<string>? Tags, IReadOnlyList<string>? AiTags,
    bool? IsFavourite, bool? IsArchived, Patch<string?> Summary, DateTimeOffset? ExpectedUpdatedAt);

public enum BulkAction { Move, AddTags, RemoveTags, Delete, Restore }

public sealed record BulkRequest(BulkAction? Action, IReadOnlyList<Guid>? Ids, Patch<Guid?> CollectionId, IReadOnlyList<string>? Tags);

public sealed record BulkResult(int Changed);

public sealed record DuplicateCheckDto(ResourceDto? Existing);

// ---- listing and search ----
public enum ResourceSort { Relevance, Newest, Oldest, Title, Updated }

/// <summary>Already validated by the service; the search implementation only has to run it.</summary>
public sealed record ResourceFilter(
    string? Query, IReadOnlyList<ResourceType> Types, IReadOnlyList<string> Tags, Guid? CollectionId, bool Inbox,
    bool? Favourite, bool Archived, DateTimeOffset? CreatedFrom, DateTimeOffset? CreatedTo, ResourceSort Sort,
    int PageNumber, int PageSize);

/// <summary>Raw query-string values for GET /resources, as the client sent them.</summary>
public sealed record ResourceListRequest(
    string? Q, IReadOnlyList<string>? Type, IReadOnlyList<string>? Tag, string? CollectionId, bool? Favourite,
    bool? Archived, DateTimeOffset? CreatedFrom, DateTimeOffset? CreatedTo, string? Sort, int? Page, int? PageSize);

// ---- tags ----
public sealed record TagDto(Guid Id, string Name, int ResourceCount);

public sealed record TagListDto(IReadOnlyList<TagDto> Items);

public sealed record RenameTagRequest(string? Name);

public sealed record MergeTagRequest(Guid? TargetTagId);

/// <summary>What a link would look like if saved. On failure the fields are empty and <see cref="Error"/> says why.</summary>
public sealed record MetadataPreviewDto(
    string? Title, string? Description, string? ImageUrl, string? FaviconUrl, ResourceType? Type, MetadataError? Error);

public sealed record MetadataPreviewRequest(string? Url);
