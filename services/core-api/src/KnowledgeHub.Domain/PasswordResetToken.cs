namespace KnowledgeHub.Domain;

/// <summary>Single-use, 30-minute password reset token. Only its SHA-256 hash is stored (NFR-SEC-02).</summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid UserId { get; init; }
    public string TokenHash { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;
}
