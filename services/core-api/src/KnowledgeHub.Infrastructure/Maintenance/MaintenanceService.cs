using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Infrastructure.Maintenance;

/// <summary>How long things are kept (SRS Appendix C). Days unless the name says hours.</summary>
public sealed class MaintenanceOptions
{
    public const string Section = "Maintenance";

    public bool Enabled { get; set; } = true;
    public double IntervalMinutes { get; set; } = 60;
    /// <summary>Deleted resources can be restored for this long, then they are removed for good (FR-RES-05).</summary>
    public int DeletedResourceDays { get; set; } = 7;
    /// <summary>Expired or revoked sign-in tokens are kept this long after, so reuse of an old one can still be spotted.</summary>
    public int RefreshTokenDays { get; set; } = 7;
    public int PasswordResetTokenHours { get; set; } = 24;
    public int OutboxDoneDays { get; set; } = 7;
    public int OutboxDeadDays { get; set; } = 30;
    public int AuditDays { get; set; } = 90;
}

public sealed record MaintenanceReport(int Resources, int RefreshTokens, int PasswordResetTokens, int OutboxDone, int OutboxDead, int AuditEvents)
{
    public int Total => Resources + RefreshTokens + PasswordResetTokens + OutboxDone + OutboxDead + AuditEvents;
}

/// <summary>Housekeeping: removes data that has outlived its retention period. Every step is a single set-based delete, safe to repeat.</summary>
public sealed partial class MaintenanceService(
    AppDbContext db, IOptions<MaintenanceOptions> options, ILogger<MaintenanceService> logger)
{
    public async Task<MaintenanceReport> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        var resourceCutoff = now.AddDays(-o.DeletedResourceDays);
        var tokenCutoff = now.AddDays(-o.RefreshTokenDays);
        var resetCutoff = now.AddHours(-o.PasswordResetTokenHours);
        var doneCutoff = now.AddDays(-o.OutboxDoneDays);
        var deadCutoff = now.AddDays(-o.OutboxDeadDays);
        var auditCutoff = now.AddDays(-o.AuditDays);

        var resources = await PurgeDeletedResourcesAsync(now, resourceCutoff, ct);
        var refresh = await db.RefreshTokens
            .Where(t => t.ExpiresAt < tokenCutoff || (t.RevokedAt != null && t.RevokedAt < tokenCutoff)).ExecuteDeleteAsync(ct);
        var reset = await db.PasswordResetTokens.Where(t => t.CreatedAt < resetCutoff).ExecuteDeleteAsync(ct);
        var done = await db.Outbox
            .Where(m => m.Status == OutboxStatus.Done && (m.ProcessedAt ?? m.CreatedAt) < doneCutoff).ExecuteDeleteAsync(ct);
        var dead = await db.Outbox
            .Where(m => m.Status == OutboxStatus.Dead && (m.ProcessedAt ?? m.CreatedAt) < deadCutoff).ExecuteDeleteAsync(ct);
        var audit = await db.AuditEvents.Where(e => e.CreatedAt < auditCutoff).ExecuteDeleteAsync(ct);

        var report = new MaintenanceReport(resources, refresh, reset, done, dead, audit);
        if (report.Total > 0) LogRemoved(report.Resources, report.RefreshTokens, report.PasswordResetTokens, report.OutboxDone + report.OutboxDead, report.AuditEvents);
        return report;
    }

    /// <summary>
    /// Removes resources deleted before the cutoff, in batches. Each batch also queues <c>resource.purged</c> in the
    /// same transaction, so the AI service forgets the page text that it kept for a possible restore.
    /// </summary>
    private async Task<int> PurgeDeletedResourcesAsync(DateTimeOffset now, DateTimeOffset cutoff, CancellationToken ct)
    {
        var total = 0;
        while (true)
        {
            var batch = await db.Resources.IgnoreQueryFilters()
                .Where(r => r.DeletedAt != null && r.DeletedAt < cutoff)
                .OrderBy(r => r.Id).Select(r => new { r.Id, r.UserId }).Take(500).ToListAsync(ct);
            if (batch.Count == 0) return total;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var r in batch)
                db.Outbox.Add(new OutboxMessage
                {
                    Type = OutboxEventTypes.ResourcePurged,
                    Payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, Guid> { ["resource_id"] = r.Id, ["user_id"] = r.UserId }),
                    NextAttemptAt = now,
                    CreatedAt = now,
                });
            await db.SaveChangesAsync(ct);
            var ids = batch.Select(r => r.Id).ToList();
            total += await db.Resources.IgnoreQueryFilters().Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Maintenance removed {Resources} deleted resources, {RefreshTokens} sign-in tokens, {ResetTokens} reset tokens, {Outbox} outbox messages, {Audit} audit events")]
    private partial void LogRemoved(int resources, int refreshTokens, int resetTokens, int outbox, int audit);
}
