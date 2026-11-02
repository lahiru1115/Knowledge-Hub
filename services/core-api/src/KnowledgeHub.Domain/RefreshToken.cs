namespace KnowledgeHub.Domain;

/// <summary>Only the SHA-256 hash of the token is stored (NFR-SEC-02). Tokens from one sign-in share a family.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string TokenHash { get; init; } = "";
    public Guid FamilyId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
}
