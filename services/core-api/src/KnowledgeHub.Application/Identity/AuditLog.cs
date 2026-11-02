using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;

namespace KnowledgeHub.Application.Identity;

/// <summary>Adds security events (NFR-SEC-13) to the current unit of work. Nothing sensitive is ever passed in.</summary>
public sealed class AuditLog(IAppDbContext db, IClock clock, IRequestContext request)
{
    public void Write(string type, Guid? userId, object? data = null)
    {
        var payload = new Dictionary<string, object?> { ["trace_id"] = request.TraceId };
        if (data is not null)
            foreach (var p in data.GetType().GetProperties())
                payload[p.Name] = p.GetValue(data);
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = userId,
            Type = type,
            Ip = System.Net.IPAddress.TryParse(request.Ip, out var ip) ? ip : null,
            UserAgent = request.UserAgent is { Length: > 512 } ua ? ua[..512] : request.UserAgent,
            Data = JsonSerializer.Serialize(payload),
            CreatedAt = clock.UtcNow,
        });
    }
}
