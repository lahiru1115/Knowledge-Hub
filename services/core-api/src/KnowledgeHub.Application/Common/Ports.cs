using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Common;

public interface IClock { DateTimeOffset UtcNow { get; } }

public interface ICurrentUser
{
    Guid? UserId { get; }
    bool IsAdmin { get; }
}

/// <summary>Request facts the Application layer needs for audit records.</summary>
public interface IRequestContext
{
    string? Ip { get; }
    string? UserAgent { get; }
    string? TraceId { get; }
}

public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database { get; }
    Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
    /// <summary>True when the hash uses older parameters and should be recomputed on next sign-in.</summary>
    bool NeedsRehash(string hash);
}
