using KnowledgeHub.Domain;

namespace KnowledgeHub.Application.Library;

/// <summary>What could be read from a page. Every field is optional because pages are often incomplete.</summary>
public sealed record PageMetadata(
    string? Title, string? Description, string? ImageUrl, string? FaviconUrl, string? SiteName,
    ResourceType Type, string? Text);

/// <summary>
/// The outcome of one fetch. <see cref="Transient"/> errors (timeouts, connection failures, 5xx) are worth retrying;
/// the others will not change by waiting (blocked address, not a web page, too large).
/// </summary>
public sealed record MetadataResult(PageMetadata? Metadata, MetadataError? Error, bool Transient)
{
    public static MetadataResult Ok(PageMetadata m) => new(m, null, false);
    public static MetadataResult Fail(MetadataError e, bool transient = false) => new(null, e, transient);
}

/// <summary>Fetches a public web page and reads its details. Implementations must be safe against SSRF (Doc 02 §5.4).</summary>
public interface IMetadataFetcher
{
    Task<MetadataResult> FetchAsync(string url, CancellationToken ct);
}
