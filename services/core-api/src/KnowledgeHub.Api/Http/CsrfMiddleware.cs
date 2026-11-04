using System.Security.Cryptography;
using System.Text;
using KnowledgeHub.Application.Common;

namespace KnowledgeHub.Api.Http;

/// <summary>
/// Double-submit CSRF check (Doc 02 §9.3, NFR-SEC-04): every POST, PUT, PATCH and DELETE under /api must send
/// <c>X-CSRF-Token</c> equal to the <c>kh_csrf</c> cookie.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (RequiresCheck(http.Request) && !HasValidToken(http.Request))
        {
            await ProblemWriter.WriteAsync(http, ProblemCode.CsrfFailed);
            return;
        }

        await next(http);
    }

    private static bool RequiresCheck(HttpRequest r) =>
        r.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !HttpMethods.IsGet(r.Method) && !HttpMethods.IsHead(r.Method) && !HttpMethods.IsOptions(r.Method);

    private static bool HasValidToken(HttpRequest r)
    {
        var header = r.Headers[AuthCookies.CsrfHeader].ToString();
        if (!r.Cookies.TryGetValue(AuthCookies.Csrf, out var cookie) || header.Length == 0 || cookie.Length == 0) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(cookie));
    }
}
