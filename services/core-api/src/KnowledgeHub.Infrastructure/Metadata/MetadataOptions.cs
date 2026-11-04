using KnowledgeHub.Domain;

namespace KnowledgeHub.Infrastructure.Metadata;

public sealed class MetadataOptions
{
    public const string Section = "Metadata";

    /// <summary>Time allowed for the whole fetch, redirects included (Doc 02 §5.4).</summary>
    public int TimeoutSeconds { get; set; } = 5;
    public int MaxBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxRedirects { get; set; } = 5;
    public string UserAgent { get; set; } = "KnowledgeHubBot/1.0 (link preview)";

    /// <summary>Rules that guess a resource's type from its address (FR-META-02). Empty means the built-in rules.</summary>
    public List<TypeRule> TypeRules { get; set; } = [];

    public static IReadOnlyList<TypeRule> DefaultTypeRules { get; } =
    [
        new() { Type = ResourceType.Video, Hosts = ["youtube.com", "youtu.be", "vimeo.com", "twitch.tv", "dailymotion.com"] },
        new() { Type = ResourceType.Repo, Hosts = ["github.com", "gitlab.com", "bitbucket.org", "codeberg.org"], PathPattern = "^/[^/]+/[^/]+/?$" },
        new() { Type = ResourceType.Course, Hosts = ["coursera.org", "udemy.com", "edx.org", "pluralsight.com", "udacity.com", "khanacademy.org", "skillshare.com"] },
        new() { Type = ResourceType.Article, Hosts = ["medium.com", "dev.to", "substack.com", "wikipedia.org", "hashnode.dev"] },
    ];
}

/// <summary>A host (or any subdomain of it), optionally narrowed by a path pattern, that maps to a resource type.</summary>
public sealed class TypeRule
{
    public ResourceType Type { get; set; }
    public List<string> Hosts { get; set; } = [];
    /// <summary>Regular expression the URL path must match; empty matches any path.</summary>
    public string? PathPattern { get; set; }
}
