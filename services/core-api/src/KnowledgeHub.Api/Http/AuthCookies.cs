using System.Security.Cryptography;
using KnowledgeHub.Application.Identity;

namespace KnowledgeHub.Api.Http;

/// <summary>Cookie names, paths and attributes from Doc 02 §9.1 and NFR-SEC-03.</summary>
public static class AuthCookies
{
    public const string Access = "kh_at";
    public const string Refresh = "kh_rt";
    public const string Csrf = "kh_csrf";
    public const string CsrfHeader = "X-CSRF-Token";

    private const string AccessPath = "/api";
    private const string RefreshPath = "/api/v1/auth";

    public static void IssueSession(HttpContext http, IssuedTokens tokens, bool secure)
    {
        http.Response.Cookies.Append(Access, tokens.AccessToken, Options(AccessPath, secure, httpOnly: true, tokens.AccessExpiresAt));
        http.Response.Cookies.Append(Refresh, tokens.RefreshToken, Options(RefreshPath, secure, httpOnly: true, tokens.RefreshExpiresAt));
        IssueCsrf(http, secure);
    }

    /// <summary>A fresh CSRF token readable by the SPA (session cookie).</summary>
    public static void IssueCsrf(HttpContext http, bool secure) =>
        http.Response.Cookies.Append(Csrf, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            Options("/", secure, httpOnly: false, expires: null));

    public static void ClearSession(HttpContext http, bool secure)
    {
        http.Response.Cookies.Delete(Access, Options(AccessPath, secure, true, null));
        http.Response.Cookies.Delete(Refresh, Options(RefreshPath, secure, true, null));
    }

    private static CookieOptions Options(string path, bool secure, bool httpOnly, DateTimeOffset? expires) => new()
    {
        HttpOnly = httpOnly,
        Secure = secure,
        SameSite = SameSiteMode.Lax,
        Path = path,
        Expires = expires,
        IsEssential = true,
    };
}
