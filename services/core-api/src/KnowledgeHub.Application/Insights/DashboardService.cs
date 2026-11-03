using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Insights;

public sealed record DashboardTotals(int Resources, int Collections, int Tags, int Favourites);
public sealed record ByTypeDto(int Article, int Video, int Pdf, int Course, int Repo, int Website, int Other);
public sealed record WeekDto(DateOnly WeekStart, int Count);
public sealed record OnboardingSteps(bool FirstResource, bool FirstCollection, bool TriedSearch, bool AskedQuestion);
public sealed record OnboardingDto(bool Dismissed, OnboardingSteps Steps);
public sealed record DashboardDto(
    DashboardTotals Totals, ByTypeDto ByType, IReadOnlyList<ResourceDto> Recent, IReadOnlyList<WeekDto> WeeklyActivity,
    OnboardingDto Onboarding);

/// <summary>
/// The home screen's numbers (FR-DASH-01 to 05). Counts cover the resources the Library lists by default: not deleted
/// and not archived, so each stat matches what its link shows, and the types add up to the total.
/// </summary>
public sealed class DashboardService(IAppDbContext db, IClock clock)
{
    public const int Weeks = 12;
    public const int RecentCount = 5;

    public async Task<DashboardDto> GetAsync(Guid userId, string? timeZone, CancellationToken ct)
    {
        var zone = ResolveZone(timeZone);
        var live = db.Resources.OwnedBy(userId).AsNoTracking().Where(r => !r.IsArchived);

        var byType = await live.GroupBy(r => r.Type).Select(g => new { Type = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(ResourceType t) => byType.FirstOrDefault(x => x.Type == t)?.Count ?? 0;
        var total = byType.Sum(x => x.Count);
        var favourites = await live.CountAsync(r => r.IsFavourite, ct);
        var collections = await db.Collections.OwnedBy(userId).CountAsync(ct);
        var tags = await db.Tags.OwnedBy(userId).CountAsync(ct);

        var recent = await live.Include(r => r.ResourceTags).ThenInclude(rt => rt.Tag)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(RecentCount).ToListAsync(ct);

        var weeks = await WeeklyActivityAsync(userId, zone, ct);

        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.TriedSearch, u.AskedQuestion, u.Preferences })
            .FirstOrDefaultAsync(ct) ?? throw new AppException(ProblemCode.NotFound);
        // "Save your first resource" stays ticked after it is deleted: the user did do it.
        var everSaved = await db.Resources.IgnoreQueryFilters().OwnedBy(userId).AnyAsync(ct);
        var steps = new OnboardingSteps(everSaved, collections > 0, user.TriedSearch, user.AskedQuestion);

        return new DashboardDto(
            new DashboardTotals(total, collections, tags, favourites),
            new ByTypeDto(Of(ResourceType.Article), Of(ResourceType.Video), Of(ResourceType.Pdf), Of(ResourceType.Course),
                Of(ResourceType.Repo), Of(ResourceType.Website), Of(ResourceType.Other)),
            [.. recent.Select(ResourceService.ToDto)],
            weeks,
            new OnboardingDto(user.Preferences.OnboardingDismissed, steps));
    }

    /// <summary>Resources added in each of the last 12 weeks (Monday to Sunday) in the user's time zone, oldest first, zeros included.</summary>
    private async Task<IReadOnlyList<WeekDto>> WeeklyActivityAsync(Guid userId, TimeZoneInfo zone, CancellationToken ct)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);
        var thisWeek = StartOfWeek(DateOnly.FromDateTime(nowLocal.DateTime));
        var first = thisWeek.AddDays(-7 * (Weeks - 1));
        // A little earlier than needed, so a time zone behind UTC cannot cut off the first week; the exact bucket is worked out below.
        var since = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1);
        var created = await db.Resources.OwnedBy(userId).AsNoTracking().Where(r => r.CreatedAt >= since)
            .Select(r => r.CreatedAt).ToListAsync(ct);

        var counts = new int[Weeks];
        foreach (var c in created)
        {
            var week = StartOfWeek(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(c, zone).DateTime));
            var index = (week.DayNumber - first.DayNumber) / 7;
            if (index is >= 0 and < Weeks) counts[index]++;
        }
        return [.. Enumerable.Range(0, Weeks).Select(i => new WeekDto(first.AddDays(7 * i), counts[i]))];
    }

    /// <summary>The Monday on or before the date.</summary>
    public static DateOnly StartOfWeek(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw AppException.Field("time_zone", "Use a time zone name such as Europe/London.");
        }
    }
}
