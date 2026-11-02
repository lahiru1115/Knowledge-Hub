namespace KnowledgeHub.Domain;

/// <summary>Security event (NFR-SEC-13). Never holds passwords, tokens or content.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid? UserId { get; init; }
    public string Type { get; init; } = "";
    public System.Net.IPAddress? Ip { get; init; }
    public string? UserAgent { get; init; }
    /// <summary>Extra non-sensitive details as a JSON object.</summary>
    public string Data { get; init; } = "{}";
    public DateTimeOffset CreatedAt { get; init; }
}
