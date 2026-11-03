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
    }
}
