using System.Net;
using System.Net.Http.Headers;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Infrastructure.Metadata;

/// <summary>
/// Fetches a page with the protections of Doc 02 §5.4: only http(s) on 80/443, every address checked (see
/// <see cref="SafeConnector"/>), redirects followed by hand and re-checked, 5 s overall, 5 MB at most, HTML and PDF only.
/// </summary>
public sealed class MetadataFetcher : IMetadataFetcher, IDisposable
{
    private static readonly string[] HtmlTypes = ["text/html", "application/xhtml+xml"];

    private readonly ISsrfPolicy _policy;
    private readonly MetadataOptions _options;
    private readonly HtmlMetadataParser _parser;
    private readonly HttpClient _http;

    public MetadataFetcher(ISsrfPolicy policy, IHostResolver resolver, IOptions<MetadataOptions> options, HtmlMetadataParser parser)
    {
        _policy = policy;
        _options = options.Value;
        _parser = parser;
        var connector = new SafeConnector(policy, resolver);
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,                   // redirects are followed below, each one checked
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (ctx, ct) => await connector.ConnectAsync(ctx.DnsEndPoint, ct),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        }, disposeHandler: true)
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<MetadataResult> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var current)) return MetadataResult.Fail(MetadataError.BlockedUrl);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            for (var hop = 0; hop <= _options.MaxRedirects; hop++)
            {
                if (_policy.CheckUri(current) is { } blocked) return MetadataResult.Fail(blocked);

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/pdf;q=0.9");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);

                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    if (location is null) return MetadataResult.Fail(MetadataError.HttpError);
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }
                if (status >= 500 || status is 408 or 429) return MetadataResult.Fail(MetadataError.HttpError, transient: true);
                if (status >= 400) return MetadataResult.Fail(MetadataError.HttpError);
                if (status != 200) return MetadataResult.Fail(MetadataError.HttpError);

                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                var isPdf = mediaType == "application/pdf";
                if (!isPdf && !HtmlTypes.Contains(mediaType)) return MetadataResult.Fail(MetadataError.NotHtml);
                if (response.Content.Headers.ContentLength > _options.MaxBytes) return MetadataResult.Fail(MetadataError.TooLarge);

                if (isPdf) return MetadataResult.Ok(HtmlMetadataParser.ForPdf(current));   // not downloaded: the headers are enough
                var body = await ReadCappedAsync(response.Content, budget.Token);
                if (body is null) return MetadataResult.Fail(MetadataError.TooLarge);
                var meta = await _parser.ParseAsync(current, body, response.Content.Headers.ContentType?.CharSet, mediaType, budget.Token);
                return MetadataResult.Ok(meta);
            }
            return MetadataResult.Fail(MetadataError.TooManyRedirects);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MetadataResult.Fail(MetadataError.Timeout, transient: true);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UriFormatException && !ct.IsCancellationRequested)
        {
            return IsBlocked(e)
                ? MetadataResult.Fail(MetadataError.BlockedUrl)
                : MetadataResult.Fail(MetadataError.Unreachable, transient: true);
        }
    }

    private static bool IsBlocked(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is SsrfBlockedException) return true;
        return false;
    }

    /// <summary>Reads at most the limit; null when the body is larger.</summary>
    private async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > _options.MaxBytes) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
