using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using SmartReader;

namespace KnowledgeHub.Infrastructure.Metadata;

/// <summary>Reads a page's title, description, images and main text. Never throws on odd markup: missing parts are just null.</summary>
public sealed class HtmlMetadataParser(TypeGuesser guesser)
{
    public async Task<PageMetadata> ParseAsync(Uri finalUrl, byte[] body, string? charset, string? contentType, CancellationToken ct)
    {
        var html = Decode(body, charset);
        var doc = await new HtmlParser().ParseDocumentAsync(html, ct);

        string? Meta(string key) =>
            Clean(doc.QuerySelectorAll("meta").FirstOrDefault(m =>
                string.Equals(m.GetAttribute("property"), key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.GetAttribute("name"), key, StringComparison.OrdinalIgnoreCase))?.GetAttribute("content"));

        var title = Meta("og:title") ?? Meta("twitter:title") ?? Clean(doc.QuerySelector("title")?.TextContent);
        var description = Meta("og:description") ?? Meta("twitter:description") ?? Meta("description");
        var image = Absolute(finalUrl, Meta("og:image") ?? Meta("twitter:image") ?? Meta("twitter:image:src"));
        var siteName = Meta("og:site_name") ?? finalUrl.IdnHost.RemovePrefix("www.");
        var favicon = Absolute(finalUrl, FaviconHref(doc)) ?? Absolute(finalUrl, "/favicon.ico");
        var type = guesser.Guess(finalUrl, contentType, Meta("og:type"));

        string? text = null;
        try
        {
            var article = new Reader(finalUrl.ToString(), html).GetArticle();
            if (article.IsReadable)
            {
                text = Clean(article.TextContent);
                title ??= Clean(article.Title);
                if (type == ResourceType.Website && text is { Length: > 1500 }) type = ResourceType.Article;
            }
        }
        catch (Exception)
        {
            // A page the reader cannot make sense of still has its title and images.
        }
        return new PageMetadata(title, description, image, favicon, siteName, type, text);
    }

    /// <summary>A PDF has no markup to read: use the file name as the title.</summary>
    public static PageMetadata ForPdf(Uri finalUrl)
    {
        var name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(finalUrl.AbsolutePath));
        return new PageMetadata(Clean(name.Replace('_', ' ').Replace('-', ' ')), null, null,
            Absolute(finalUrl, "/favicon.ico"), finalUrl.IdnHost.RemovePrefix("www."), ResourceType.Pdf, null);
    }

    private static string? FaviconHref(IDocument doc)
    {
        var links = doc.QuerySelectorAll("link[rel]").OfType<IHtmlLinkElement>()
            .Where(l => !string.IsNullOrWhiteSpace(l.GetAttribute("href"))).ToList();
        IHtmlLinkElement? Pick(Func<string, bool> rel) =>
            links.FirstOrDefault(l => l.GetAttribute("rel")!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(rel));
        return (Pick(r => r.Equals("icon", StringComparison.OrdinalIgnoreCase))
                ?? Pick(r => r.Equals("shortcut", StringComparison.OrdinalIgnoreCase))
                ?? Pick(r => r.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase)))?.GetAttribute("href");
    }

    private static string Decode(byte[] body, string? charset)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(charset)) return System.Text.Encoding.GetEncoding(charset).GetString(body);
        }
        catch (ArgumentException)
        {
            // Unknown charset name: fall through to UTF-8.
        }
        return System.Text.Encoding.UTF8.GetString(body);
    }

    /// <summary>Collapses whitespace and returns null for empty text.</summary>
    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Resolves a possibly relative address against the page. Only web addresses are kept.</summary>
    private static string? Absolute(Uri baseUri, string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        return Uri.TryCreate(baseUri, href.Trim(), out var u) && u.Scheme is "http" or "https" && u.AbsoluteUri.Length <= 2048
            ? u.AbsoluteUri : null;
    }
}

internal static class StringExtensions
{
    public static string RemovePrefix(this string s, string prefix) =>
        s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? s[prefix.Length..] : s;
}
