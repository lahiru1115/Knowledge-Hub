using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;

namespace KnowledgeHub.Application.Library;

/// <summary>Shows what a link looks like before it is saved (FR-RES-01 quick add). Nothing is stored.</summary>
public sealed class MetadataPreviewService(IMetadataFetcher fetcher)
{
    public async Task<MetadataPreviewDto> PreviewAsync(string? url, CancellationToken ct)
    {
        if (!UrlNormalizer.IsValid(url))
            throw AppException.Field("url", "Enter a link that starts with http:// or https:// (up to 2,048 characters).");
        var result = await fetcher.FetchAsync(url!.Trim(), ct);
        return result.Metadata is { } m
            ? new MetadataPreviewDto(m.Title, m.Description, m.ImageUrl, m.FaviconUrl, m.Type, null)
            : new MetadataPreviewDto(null, null, null, null, null, result.Error);
    }
}
