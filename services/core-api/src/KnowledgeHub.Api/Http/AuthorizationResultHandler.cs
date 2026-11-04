using KnowledgeHub.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace KnowledgeHub.Api.Http;

/// <summary>Turns authentication and authorisation failures into Problem Details (401 / 403).</summary>
public sealed class AuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged) { await ProblemWriter.WriteAsync(context, ProblemCode.Unauthenticated); return; }
        if (authorizeResult.Forbidden) { await ProblemWriter.WriteAsync(context, ProblemCode.Forbidden); return; }
        await next(context);
    }
}
