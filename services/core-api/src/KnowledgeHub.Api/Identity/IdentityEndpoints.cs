using KnowledgeHub.Api.Http;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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
        auth.MapPost("/password-reset", RequestPasswordReset).RequireRateLimiting(RateLimiting.ResetPerIp);
        auth.MapPost("/password-reset/confirm", ConfirmPasswordReset);

        var me = api.MapGroup("/me").RequireAuthorization();
        me.MapGet("", Me);
        me.MapPatch("", UpdateMe);
        me.MapDelete("", DeleteMe);
        me.MapPut("/password", ChangePassword);
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

    private static async Task<Accepted> RequestPasswordReset(PasswordResetRequest body, AccountService account, CancellationToken ct)
    {
        await account.RequestPasswordResetAsync(body, ct);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<NoContent> ConfirmPasswordReset(PasswordResetConfirmRequest body, AccountService account, CancellationToken ct)
    {
        await account.ConfirmPasswordResetAsync(body, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<UserDto>> UpdateMe(UpdateMeRequest body, ICurrentUser user, AccountService account, CancellationToken ct) =>
        TypedResults.Ok(await account.UpdateMeAsync(user.UserId!.Value, body, ct));

    private static async Task<NoContent> ChangePassword(
        ChangePasswordRequest body, HttpContext http, ICurrentUser user, AccountService account,
        IOptions<AuthOptions> options, CancellationToken ct)
    {
        var tokens = await account.ChangePasswordAsync(user.UserId!.Value, body, ct);
        AuthCookies.IssueSession(http, tokens, options.Value.SecureCookies);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> DeleteMe(
        [FromBody] DeleteAccountRequest? body, HttpContext http, ICurrentUser user, AccountService account,
        IOptions<AuthOptions> options, CancellationToken ct)
    {
        await account.DeleteAccountAsync(user.UserId!.Value, body?.Password, ct);
        AuthCookies.ClearSession(http, options.Value.SecureCookies);
        return TypedResults.NoContent();
    }
}
