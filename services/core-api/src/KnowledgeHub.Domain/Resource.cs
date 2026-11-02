namespace KnowledgeHub.Domain;

public enum SummarySource { None, Extracted, Ai, User }

public enum MetadataStatus { Pending, Ready, Failed }

public sealed class Resource : IOwned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    /// <summary>Null means Inbox (FR-RES-06).</summary>
    public Guid? CollectionId { get; set; }
    public string Url { get; set; } = "";
    public string UrlNormalized { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public ResourceType Type { get; set; } = ResourceType.Website;
    public string? FaviconUrl { get; set; }
    public string? ImageUrl { get; set; }
    public string? SiteName { get; set; }
    public string? Summary { get; set; }
    public SummarySource SummarySource { get; set; } = SummarySource.None;
    public bool IsFavourite { get; set; }
    public bool IsArchived { get; set; }
    public MetadataStatus MetadataStatus { get; set; } = MetadataStatus.Pending;
    /// <summary>
    /// The tag names joined by spaces; kept in step with the tags and indexed for search (Doc 02 §7.2). The database
    /// builds the weighted search vector from this and the other text fields; the model maps it as a shadow column.
    /// </summary>
    public string TagsText { get; set; } = "";
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ResourceTag> ResourceTags { get; } = [];
}
