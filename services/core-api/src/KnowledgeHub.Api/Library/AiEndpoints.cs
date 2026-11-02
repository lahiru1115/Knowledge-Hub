using KnowledgeHub.Application.Ai;
using KnowledgeHub.Application.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace KnowledgeHub.Api.Library;

/// <summary>AI features for signed-in users (Doc 02 §8.2). Summaries are stored on the resource; nothing is applied silently.</summary>
public static class AiEndpoints
{
    public static void MapAi(this RouteGroupBuilder api)
    {
        api.MapPost("/resources/{id:guid}/summary", async (Guid id, ICurrentUser u, AiFeaturesService s, CancellationToken ct) =>
            TypedResults.Ok(await s.SummarizeAsync(u.UserId!.Value, id, ct))).RequireAuthorization();
        api.MapDelete("/resources/{id:guid}/summary", async (Guid id, ICurrentUser u, AiFeaturesService s, CancellationToken ct) =>
        {
            await s.RemoveSummaryAsync(u.UserId!.Value, id, ct);
            return TypedResults.NoContent();
        }).RequireAuthorization();
        api.MapPost("/ai/tag-suggestions", async (TagSuggestionRequest body, ICurrentUser u, AiFeaturesService s, CancellationToken ct) =>
            TypedResults.Ok(await s.SuggestTagsAsync(u.UserId!.Value, body, ct))).RequireAuthorization();
        api.MapGet("/search/semantic", async (string? q, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "page_size")] int? pageSize,
                ICurrentUser u, AiFeaturesService s, CancellationToken ct) =>
            TypedResults.Ok(await s.SemanticSearchAsync(u.UserId!.Value, q, pageSize, ct))).RequireAuthorization();
        api.MapGet("/resources/{id:guid}/related", async (Guid id, ICurrentUser u, AiFeaturesService s, CancellationToken ct) =>
            TypedResults.Ok(await s.RelatedAsync(u.UserId!.Value, id, ct))).RequireAuthorization();
        api.MapGet("/ai/status", async (AiFeaturesService s, CancellationToken ct) =>
            TypedResults.Ok(await s.StatusAsync(ct))).RequireAuthorization();
        api.MapGet("/me/ai-usage", async (ICurrentUser u, AiFeaturesService s, CancellationToken ct) =>
            TypedResults.Ok(await s.UsageAsync(u.UserId!.Value, ct))).RequireAuthorization();
    }
}
