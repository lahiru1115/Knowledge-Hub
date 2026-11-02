using KnowledgeHub.Api.Http;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Api.Identity;

/// <summary>Identity module endpoints (Doc 02 §3.2, §8.2). Password reset, change, profile update and deletion follow.</summary>
public static class IdentityEndpoints
{
    public static void MapIdentity(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");
        auth.MapGet("/csrf", Csrf);
        auth.MapPost("/register", Register).RequireRateLimiting(RateLimiting.RegisterPerIp);
        auth.MapPost("/login", Login).RequireRateLimiting(RateLimiting.LoginPerIp);
        auth.MapPost("/logout", Logout).RequireAuthorization();
        auth.MapPost("/refresh", Refresh);

        api.MapGet("/me", Me).RequireAuthorization();
    }

    private static NoContent Csrf(HttpContext http, IOptions<AuthOptions> options)
    {
        AuthCookies.IssueCsrf(http, options.Value.SecureCookies);
        return TypedResults.NoContent();
    }

    private static async Task<Created<UserDto>> Register(
        RegisterRequest body, HttpContext http, AuthService auth, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var result = await auth.RegisterAsync(body, ct);
        AuthCookies.IssueSession(http, result.Tokens, options.Value.SecureCookies);
        return TypedResults.Created("/api/v1/me", result.User);
    }

    private static async Task<Ok<UserDto>> Login(
        LoginRequest body, HttpContext http, AuthService auth, LoginThrottle throttle, IRequestContext request,
        IOptions<AuthOptions> options, CancellationToken ct)
    {
        throttle.Acquire(request.Ip, body.Email ?? "");
        var result = await auth.LoginAsync(body, ct);
        AuthCookies.IssueSession(http, result.Tokens, options.Value.SecureCookies);
        return TypedResults.Ok(result.User);
    }

    private static async Task<NoContent> Logout(
        HttpContext http, AuthService auth, ICurrentUser user, IOptions<AuthOptions> options, CancellationToken ct)
    {
        await auth.LogoutAsync(user.UserId!.Value, http.Request.Cookies[AuthCookies.Refresh], ct);
        AuthCookies.ClearSession(http, options.Value.SecureCookies);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> Refresh(
        HttpContext http, AuthService auth, IOptions<AuthOptions> options, CancellationToken ct)
    {
        try
        {
            var tokens = await auth.RefreshAsync(http.Request.Cookies[AuthCookies.Refresh], ct);
            AuthCookies.IssueSession(http, tokens, options.Value.SecureCookies);
            return TypedResults.NoContent();
        }
        catch (AppException)
        {
            AuthCookies.ClearSession(http, options.Value.SecureCookies);
            throw;
        }
    }

    private static async Task<Ok<UserDto>> Me(ICurrentUser user, AuthService auth, CancellationToken ct) =>
        TypedResults.Ok(await auth.GetMeAsync(user.UserId!.Value, ct));
}
