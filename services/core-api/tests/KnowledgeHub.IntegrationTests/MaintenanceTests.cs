using System.Net;
using System.Text.Json;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class MaintenanceTests(ApiFactory factory)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task<MaintenanceReport> RunAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MaintenanceService>().RunOnceAsync(Now, TestContext.Current.CancellationToken);
    }

    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    [Fact]
    public async Task Resources_deleted_more_than_seven_days_ago_are_removed_for_good_with_their_tags()
    {
        var c = await factory.UserAsync();
        var old = await c.CreateResourceAsync(extra: new { tags = new[] { "keep-me-not" } });
        var recent = await c.CreateResourceAsync();
        var live = await c.CreateResourceAsync();
        await using (var db = factory.Db.Admin())
        {
            var ct = TestContext.Current.CancellationToken;
            await db.Resources.Where(r => r.Id == Guid.Parse(Id(old))).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedAt, Now.AddDays(-7).AddMinutes(-1)), ct);
            await db.Resources.IgnoreQueryFilters().Where(r => r.Id == Guid.Parse(Id(recent))).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedAt, Now.AddDays(-7).AddMinutes(1)), ct);
        }

        var report = await RunAsync();

        Assert.True(report.Resources >= 1);
        await using var check = factory.Db.Admin();
        var ids = await check.Resources.IgnoreQueryFilters().AsNoTracking().Select(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Guid.Parse(Id(old)), ids);
        Assert.Contains(Guid.Parse(Id(recent)), ids);   // still inside the 7 days: can be restored
        Assert.Contains(Guid.Parse(Id(live)), ids);
        Assert.False(await check.ResourceTags.AnyAsync(t => t.ResourceId == Guid.Parse(Id(old)), TestContext.Current.CancellationToken));
        await c.PostAsync($"/api/v1/resources/{Id(recent)}/restore").ExpectAsync(HttpStatusCode.OK);
        await c.PostAsync($"/api/v1/resources/{Id(old)}/restore").ExpectAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Old_tokens_outbox_messages_and_audit_events_go_and_fresh_ones_stay()
    {
        var c = await factory.UserAsync();
        var me = await c.GetAsync("/api/v1/me").ExpectAsync(HttpStatusCode.OK);
        var userId = Guid.Parse(me.GetProperty("id").GetString()!);
        var ct = TestContext.Current.CancellationToken;
        var marker = Guid.NewGuid().ToString("N");

        RefreshToken Refresh(string name, DateTimeOffset expires, DateTimeOffset? revoked) =>
            new() { UserId = userId, TokenHash = $"{marker}-{name}", FamilyId = Guid.NewGuid(), CreatedAt = Now.AddDays(-30), ExpiresAt = expires, RevokedAt = revoked };
        PasswordResetToken Reset(string name, DateTimeOffset created) =>
            new() { UserId = userId, TokenHash = $"{marker}-{name}", CreatedAt = created, ExpiresAt = created.AddHours(1) };
        OutboxMessage Box(string name, OutboxStatus status, DateTimeOffset when) =>
            new() { Type = "maintenance.test", Payload = JsonSerializer.Serialize(new { name, marker }), Status = status, NextAttemptAt = when, CreatedAt = when, ProcessedAt = when };
        AuditEvent Audit(string name, DateTimeOffset when) =>
            new() { UserId = userId, Type = "maintenance.test", Data = JsonSerializer.Serialize(new { name, marker }), CreatedAt = when };

        await using (var db = factory.Db.Admin())
        {
            db.RefreshTokens.AddRange(
                Refresh("expired-old", Now.AddDays(-8), null), Refresh("expired-recent", Now.AddDays(-6), null),
                Refresh("revoked-old", Now.AddDays(5), Now.AddDays(-8)), Refresh("revoked-recent", Now.AddDays(5), Now.AddDays(-6)),
                Refresh("valid", Now.AddDays(5), null));
            db.PasswordResetTokens.AddRange(Reset("old", Now.AddHours(-25)), Reset("fresh", Now.AddHours(-23)));
            db.Outbox.AddRange(
                Box("done-old", OutboxStatus.Done, Now.AddDays(-8)), Box("done-recent", OutboxStatus.Done, Now.AddDays(-6)),
                Box("dead-old", OutboxStatus.Dead, Now.AddDays(-31)), Box("dead-recent", OutboxStatus.Dead, Now.AddDays(-29)),
                Box("pending-ancient", OutboxStatus.Pending, Now.AddDays(-60)));
            db.AuditEvents.AddRange(Audit("old", Now.AddDays(-91)), Audit("recent", Now.AddDays(-89)));
            await db.SaveChangesAsync(ct);
        }

        var report = await RunAsync();

        Assert.True(report.RefreshTokens >= 2 && report.PasswordResetTokens >= 1 && report.OutboxDone >= 1 && report.OutboxDead >= 1 && report.AuditEvents >= 1);
        await using var check = factory.Db.Admin();
        var refresh = (await check.RefreshTokens.Where(t => t.TokenHash.StartsWith(marker)).Select(t => t.TokenHash).ToListAsync(ct)).Select(h => h[(marker.Length + 1)..]).Order().ToList();
        Assert.Equal(["expired-recent", "revoked-recent", "valid"], refresh);
        var reset = (await check.PasswordResetTokens.Where(t => t.TokenHash.StartsWith(marker)).Select(t => t.TokenHash).ToListAsync(ct)).Select(h => h[(marker.Length + 1)..]).ToList();
        Assert.Equal(["fresh"], reset);
        var boxes = (await check.Outbox.Where(m => m.Type == "maintenance.test").ToListAsync(ct)).Where(m => m.Payload.Contains(marker)).Select(m => JsonDocument.Parse(m.Payload).RootElement.GetProperty("name").GetString()!).Order().ToList();
        Assert.Equal(["dead-recent", "done-recent", "pending-ancient"], boxes);   // a message still waiting is never removed
        var audits = (await check.AuditEvents.Where(e => e.Type == "maintenance.test").ToListAsync(ct)).Where(e => e.Data.Contains(marker)).Select(e => JsonDocument.Parse(e.Data).RootElement.GetProperty("name").GetString()).ToList();
        Assert.Equal(["recent"], audits);
        Assert.True(await check.Users.AnyAsync(u => u.Id == userId, ct));
    }

    [Fact]
    public async Task Running_twice_does_nothing_the_second_time()
    {
        await RunAsync();
        Assert.Equal(0, (await RunAsync()).Total);
    }
}
