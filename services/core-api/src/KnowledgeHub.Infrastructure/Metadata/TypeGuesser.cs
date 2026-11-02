using System.Text.RegularExpressions;
using KnowledgeHub.Domain;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Infrastructure.Metadata;

/// <summary>Guesses a resource's type from its address, content type and Open Graph type (FR-META-02). The rules are configuration.</summary>
public sealed class TypeGuesser(IOptions<MetadataOptions> options)
{
    private readonly IReadOnlyList<TypeRule> _rules =
        options.Value.TypeRules.Count > 0 ? options.Value.TypeRules : MetadataOptions.DefaultTypeRules;

    public ResourceType Guess(Uri url, string? contentType, string? ogType)
    {
        var host = url.IdnHost.ToLowerInvariant();
        foreach (var rule in _rules)
        {
            var hostMatches = rule.Hosts.Any(h =>
                host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
            if (!hostMatches) continue;
            if (string.IsNullOrEmpty(rule.PathPattern)
                || Regex.IsMatch(url.AbsolutePath, rule.PathPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)))
                return rule.Type;
        }
        if (contentType?.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) == true
            || url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return ResourceType.Pdf;
        if (ogType is not null)
        {
            if (ogType.StartsWith("video", StringComparison.OrdinalIgnoreCase)) return ResourceType.Video;
            if (ogType.Equals("article", StringComparison.OrdinalIgnoreCase)) return ResourceType.Article;
        }
        return ResourceType.Website;
    }
}
