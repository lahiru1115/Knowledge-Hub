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

        var resources = await db.Resources.IgnoreQueryFilters()
            .Where(r => r.DeletedAt != null && r.DeletedAt < resourceCutoff).ExecuteDeleteAsync(ct);
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Maintenance removed {Resources} deleted resources, {RefreshTokens} sign-in tokens, {ResetTokens} reset tokens, {Outbox} outbox messages, {Audit} audit events")]
    private partial void LogRemoved(int resources, int refreshTokens, int resetTokens, int outbox, int audit);
}
