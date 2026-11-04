using System.Text.RegularExpressions;

namespace KnowledgeHub.Domain;

public enum TagSource { User, Ai }

public sealed class Tag : IOwned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string Name { get; set; } = "";
}

public sealed class ResourceTag
{
    public Guid ResourceId { get; init; }
    public Guid TagId { get; init; }
    public Resource Resource { get; init; } = null!;
    public Tag Tag { get; init; } = null!;
    /// <summary>"ai" when the user accepted a suggestion; lets us measure how useful suggestions are (FR-AI-04).</summary>
    public TagSource Source { get; set; } = TagSource.User;
}

/// <summary>Tag name rules (FR-TAG-01): trimmed, lowercased, 1 to 50 characters of letters, digits, spaces and - _ . # +.</summary>
public static partial class TagNames
{
    public const int MaxLength = 50;
    public const int MaxPerResource = 20;

    public static bool TryNormalize(string? input, out string name)
    {
        // Collapse runs of spaces so "machine   learning" and "machine learning" are one tag. Tabs and line breaks
        // are not spaces (FR-TAG-01), so they are left in and fail the check below.
        name = SpaceRun().Replace((input ?? "").Trim(), " ").ToLowerInvariant();
        return name.Length is >= 1 and <= MaxLength && Allowed().IsMatch(name);
    }

    [GeneratedRegex(@"^[\p{L}\p{N} \-_.#+]+$")]
    private static partial Regex Allowed();

    [GeneratedRegex(" {2,}")]
    private static partial Regex SpaceRun();
}
