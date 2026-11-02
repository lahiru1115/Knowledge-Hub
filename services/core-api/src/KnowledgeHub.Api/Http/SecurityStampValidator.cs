using KnowledgeHub.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace KnowledgeHub.Api.Http;

/// <summary>
/// Rejects access tokens whose <c>stamp</c> claim no longer matches the user's stored security stamp, or whose
/// user is disabled or gone. The lookup is cached for 60 seconds (Doc 02 §9.1), so revocation takes effect
/// within a minute.
/// </summary>
public sealed class SecurityStampValidator(IMemoryCache cache, IAppDbContext db) : ISessionCache
{
    private static readonly TimeSpan s_ttl = TimeSpan.FromSeconds(60);
    private static string Key(Guid userId) => $"stamp:{userId}";

    /// <summary>Called after a password change, reset or deletion so the change applies at once.</summary>
    public void Forget(Guid userId) => cache.Remove(Key(userId));

    private sealed record Snapshot(Guid Stamp, bool Disabled);

    public async Task<bool> IsValidAsync(Guid userId, string? stampClaim, CancellationToken ct)
    {
        var snapshot = await cache.GetOrCreateAsync(Key(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = s_ttl;
            return await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new Snapshot(u.SecurityStamp, u.IsDisabled))
                .FirstOrDefaultAsync(ct);
        });
        return snapshot is { Disabled: false } s && Guid.TryParse(stampClaim, out var stamp) && stamp == s.Stamp;
    }
}
