using System.Security.Claims;
using System.Threading.RateLimiting;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Api.Http;

/// <summary>Rate limits from NFR-SEC-06. Policy names are used on endpoints.</summary>
public static class RateLimiting
{
    public const string LoginPerIp = "login-ip";
    public const string RegisterPerIp = "register-ip";
    public const string ResetPerIp = "reset-ip";

    public static IServiceCollection AddKnowledgeHubRateLimiting(this IServiceCollection services)
    {
        services.AddSingleton<LoginThrottle>();
        services.AddRateLimiter(o =>
        {
            o.OnRejected = async (ctx, ct) =>
            {
                var extensions = new Dictionary<string, object?>();
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    extensions["retry_after"] = (int)Math.Ceiling(retry.TotalSeconds);
                await ProblemWriter.WriteAsync(ctx.HttpContext, ProblemCode.RateLimited, extensions: extensions);
            };
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });
        // Policies read their limits from options at request time, so tests and deployments can change them.
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AuthOptions>>((o, auth) =>
        {
            var limits = auth.Value.RateLimits;
            o.AddPolicy(LoginPerIp, http => RateLimitPartition.GetFixedWindowLimiter(Ip(http),
                _ => Window(limits.LoginPerIpPerMinute, TimeSpan.FromMinutes(1))));
            o.AddPolicy(RegisterPerIp, http => RateLimitPartition.GetFixedWindowLimiter(Ip(http),
                _ => Window(limits.RegisterPerIpPerHour, TimeSpan.FromHours(1))));
            o.AddPolicy(ResetPerIp, http => RateLimitPartition.GetFixedWindowLimiter(Ip(http),
                _ => Window(limits.ResetPerIpPerHour, TimeSpan.FromHours(1))));
            // Everything else: per user when signed in, otherwise per IP.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.User.FindFirstValue("sub") ?? Ip(http),
                    _ => Window(limits.GeneralPerUserPerMinute, TimeSpan.FromMinutes(1))));
        });
        return services;
    }

    private static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) => new()
    {
        PermitLimit = permits,
        Window = window,
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}

/// <summary>Sign-in attempts per IP and email address (5 per minute by default).</summary>
public sealed class LoginThrottle(IOptions<AuthOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.Value.RateLimits.LoginPerIpAndEmailPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    public void Acquire(string? ip, string email)
    {
        using var lease = _limiter.AttemptAcquire($"{ip}|{email.Trim().ToLowerInvariant()}");
        if (lease.IsAcquired) return;
        var ex = new AppException(ProblemCode.RateLimited);
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            ex.Extensions["retry_after"] = (int)Math.Ceiling(retry.TotalSeconds);
        throw ex;
    }

    public void Dispose() => _limiter.Dispose();
}
