using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Application.Identity;

/// <summary>Creates access and refresh tokens for a sign-in and revokes refresh tokens.</summary>
public sealed class SessionIssuer(IAppDbContext db, ITokenService tokens, IOptions<AuthOptions> options)
{
    public IssuedTokens Issue(User user, Guid familyId, DateTimeOffset now) => Issue(user, familyId, now, out _);

    public IssuedTokens Issue(User user, Guid familyId, DateTimeOffset now, out RefreshToken refresh)
    {
        var access = tokens.CreateAccessToken(user);
        var raw = tokens.NewRefreshToken();
        refresh = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = tokens.HashToken(raw),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.RefreshTokenDays),
        };
        db.RefreshTokens.Add(refresh);
        return new IssuedTokens(access.Value, access.ExpiresAt, raw, refresh.ExpiresAt);
    }

    public async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        var live = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in live) t.RevokedAt = now;
    }

    public async Task RevokeAllAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var live = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in live) t.RevokedAt = now;
    }
}
