using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Insights;

public sealed record AdminUserDto(
    Guid Id, string Email, string DisplayName, string Role, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt,
    int ResourceCount, bool IsDisabled);

public sealed record AdminAiUsageRowDto(DateOnly Date, Guid? UserId, string Operation, int Calls, int TokensIn, int TokensOut, decimal CostUsd);

/// <summary>The administrator's user list and AI usage (FR-ADM-01 to 04). Authorisation is the "admin" policy on the endpoints.</summary>
public sealed class AdminService(IAppDbContext db, IClock clock, SessionIssuer sessions, ISessionCache sessionCache, AuditLog audit, IAiClient ai)
{
    public const int MaxPageSize = 100;

    public async Task<Page<AdminUserDto>> ListUsersAsync(string? q, int? page, int? pageSize, CancellationToken ct)
    {
        var number = page ?? 1;
        var size = pageSize ?? 20;
        var errors = new Dictionary<string, string[]>();
        if (number is < 1 or > Library.ResourceService.MaxPage) errors["page"] = [$"Must be between 1 and {Library.ResourceService.MaxPage}."];
        if (size is < 1 or > MaxPageSize) errors["page_size"] = [$"Must be between 1 and {MaxPageSize}."];
        if (errors.Count > 0) throw new AppException(ProblemCode.ValidationFailed, "One or more fields are not valid.") { Errors = errors };

        var query = db.Users.AsNoTracking();
        if (q?.Trim() is { Length: > 0 } term)
        {
            var lowered = term.ToLowerInvariant();
            // The comparison runs in the database; ToLower and Contains are what translate, and Contains escapes wildcards.
#pragma warning disable CA1304, CA1311, CA1862
            query = query.Where(u => u.Email.ToLower().Contains(lowered));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id)
            .Skip((number - 1) * size).Take(size)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                u.Role,
                u.CreatedAt,
                u.LastLoginAt,
                u.IsDisabled,
                Resources = db.Resources.Count(r => r.UserId == u.Id),   // the query filter leaves out deleted ones
            }).ToListAsync(ct);
        return new Page<AdminUserDto>(
            [.. rows.Select(r => new AdminUserDto(r.Id, r.Email, r.DisplayName, r.Role.ToString().ToLowerInvariant(), r.CreatedAt, r.LastLoginAt, r.Resources, r.IsDisabled))],
            number, size, total);
    }

    /// <summary>Disabling ends the user's sessions and stops sign-in; their data stays (FR-ADM-02).</summary>
    public async Task<AdminUserDto> SetDisabledAsync(Guid adminId, Guid userId, bool disabled, CancellationToken ct)
    {
        if (userId == adminId) throw AppException.Field("is_disabled", "You can't disable your own account.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new AppException(ProblemCode.NotFound);
        if (user.IsDisabled != disabled)
        {
            var now = clock.UtcNow;
            user.IsDisabled = disabled;
            user.UpdatedAt = now;
            if (disabled)
            {
                user.SecurityStamp = Guid.NewGuid();
                await sessions.RevokeAllAsync(user.Id, now, ct);
            }

            audit.Write(disabled ? "admin.user_disabled" : "admin.user_enabled", adminId, new { TargetUserId = user.Id });
            await db.SaveChangesAsync(ct);
            sessionCache.Forget(user.Id);
        }

        var resources = await db.Resources.CountAsync(r => r.UserId == user.Id, ct);
        return new AdminUserDto(user.Id, user.Email, user.DisplayName, user.Role.ToString().ToLowerInvariant(), user.CreatedAt, user.LastLoginAt, resources, user.IsDisabled);
    }

    /// <summary>A 503 problem when the AI service does not answer.</summary>
    public async Task<IReadOnlyList<AdminAiUsageRowDto>> AiUsageAsync(Guid? userId, CancellationToken ct) =>
        [.. (await Ai.AiFeaturesService.CallAsync(() => ai.GetAdminUsageAsync(userId, ct))).Select(r => new AdminAiUsageRowDto(r.Date, r.UserId, r.Operation, r.Calls, r.TokensIn, r.TokensOut, r.CostUsd))];
}
