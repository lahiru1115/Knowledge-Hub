namespace KnowledgeHub.Api.Http;

/// <summary>
/// The response headers Doc 02 §9.5 asks of the API. The API only ever sends JSON, problem documents, event streams
/// and file downloads, so its CSP can be much stricter than the one the web app needs: nothing may load or embed.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext http)
    {
        http.Response.OnStarting(() =>
        {
            var h = http.Response.Headers;
            h.TryAdd("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");
            h.TryAdd("X-Content-Type-Options", "nosniff");
            h.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
            h.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=(), usb=()");
            // Only meaningful over HTTPS, and browsers ignore it on plain HTTP; the host in front terminates TLS.
            if (http.Request.IsHttps || string.Equals(http.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase))
                h.TryAdd("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
            return Task.CompletedTask;
        });
        return next(http);
    }
}

/// <summary>
/// A request under /api that matches no endpoint (an unknown path, or an ID that is not a GUID) gets the same
/// <c>not_found</c> problem as a missing record, not an empty 404 (Doc 02 §8.4, IR-12).
/// </summary>
public sealed class UnmatchedApiRouteMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        await next(http);
        if (http.Response.StatusCode == StatusCodes.Status404NotFound && !http.Response.HasStarted
            && http.GetEndpoint() is null && http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            await ProblemWriter.WriteAsync(http, KnowledgeHub.Application.Common.ProblemCode.NotFound);
    }
}

/// <summary>
/// PostgreSQL text cannot hold a NUL character, so a request that carries one in the query string or a JSON body
/// is refused up front as a validation error, instead of failing later as a 500 when it is saved or searched.
/// </summary>
public sealed partial class NullCharacterMiddleware(RequestDelegate next)
{
    private const int BodyLimit = 2_000_000;

    // \u0000 as JSON writes it, but not when the backslash itself is escaped (\\u0000 is just text).
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\\)(?:\\\\)*\\u0000", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EscapedNul();

    public async Task InvokeAsync(HttpContext http)
    {
        var r = http.Request;
        if (r.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) && await Carries(r))
        {
            await ProblemWriter.WriteAsync(http, KnowledgeHub.Application.Common.ProblemCode.ValidationFailed,
                "One or more fields are not valid.",
                new Dictionary<string, string[]> { ["request"] = ["Text can't contain null characters."] });
            return;
        }

        await next(http);
    }

    private static async Task<bool> Carries(HttpRequest r)
    {
        if (r.QueryString.HasValue && r.QueryString.Value!.Contains("%00", StringComparison.Ordinal)) return true;
        if (r.ContentType is null || !r.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return false;
        if (r.ContentLength is null or 0 && !r.Headers.ContainsKey("Transfer-Encoding")) return false;

        r.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: BodyLimit);
        using var reader = new StreamReader(r.Body, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = await reader.ReadToEndAsync(r.HttpContext.RequestAborted);
        r.Body.Position = 0;
        return text.Contains('\0', StringComparison.Ordinal) || EscapedNul().IsMatch(text);
    }
}
