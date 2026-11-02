namespace KnowledgeHub.Domain;

/// <summary>The fixed set of collection colours from the design system (FR-COL-02, Doc 03 §7.1).</summary>
public static class CollectionColors
{
    public static readonly IReadOnlyList<string> All = ["slate", "teal", "blue", "violet", "rose", "amber", "green", "orange"];
}

public sealed class Collection : IOwned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public bool IsPinned { get; set; }
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}
