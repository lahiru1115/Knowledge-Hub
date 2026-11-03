using KnowledgeHub.Api.Http;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Insights;

namespace KnowledgeHub.Api.Library;

/// <summary>The home screen's numbers and the export (Doc 02 §8.2).</summary>
public static class InsightsEndpoints
{
    public static void MapInsights(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (string? time_zone, ICurrentUser u, DashboardService s, CancellationToken ct) =>
            TypedResults.Ok(await s.GetAsync(u.UserId!.Value, time_zone, ct))).RequireAuthorization();
        api.MapGet("/export", Export).RequireAuthorization().RequireRateLimiting(RateLimiting.ExportPerUser);
    }

    private static async Task Export(string? format, HttpContext http, ICurrentUser user, ExportService export, IClock clock)
    {
        var kind = format?.Trim().ToLowerInvariant();
        if (kind is not ("json" or "csv")) throw AppException.Field("format", "Use json or csv.");
        var ct = http.RequestAborted;
        await export.RecordAsync(user.UserId!.Value, kind, ct);

        // Headers go out first; from here on a failure can only cut the file short, so everything that can be checked is above.
        http.Response.ContentType = kind == "json" ? "application/json; charset=utf-8" : "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{ExportService.FileName(kind, clock.UtcNow)}\"";
        http.Response.Headers.CacheControl = "no-store";   // it is the user's private data
        if (kind == "json") await export.WriteJsonAsync(user.UserId!.Value, http.Response.Body, ct);
        else await export.WriteCsvAsync(user.UserId!.Value, http.Response.Body, ct);
    }
}
