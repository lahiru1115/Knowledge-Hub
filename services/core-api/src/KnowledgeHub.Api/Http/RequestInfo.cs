using System.Diagnostics;
using System.Security.Claims;
using KnowledgeHub.Application.Common;

namespace KnowledgeHub.Api.Http;

public sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    private HttpContext? Http => accessor.HttpContext;
    public string? Ip => Http?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => Http?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
    public string? TraceId => Activity.Current?.TraceId.ToString() ?? Http?.TraceIdentifier;
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;

    public bool IsAdmin => Principal?.FindFirstValue("role") == "admin";
}
