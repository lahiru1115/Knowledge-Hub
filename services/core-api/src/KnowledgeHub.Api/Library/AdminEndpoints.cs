using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Insights;

namespace KnowledgeHub.Api.Library;

/// <summary>Administrator endpoints, all behind the "admin" policy: members get 403 (FR-ADM-04).</summary>
public static class AdminEndpoints
{
    public static void MapAdmin(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").RequireAuthorization("admin");
        admin.MapGet("/users", async (string? q, int? page, int? page_size, AdminService s, CancellationToken ct) =>
            TypedResults.Ok(ToPage(await s.ListUsersAsync(q, page, page_size, ct))));
        admin.MapPatch("/users/{id:guid}", async (Guid id, AdminUserPatch body, ICurrentUser u, AdminService s, CancellationToken ct) =>
            TypedResults.Ok(await s.SetDisabledAsync(u.UserId!.Value, id, body.IsDisabled ?? throw AppException.Field("is_disabled", "Required."), ct)));
        admin.MapGet("/ai-usage", async (Guid? user_id, AdminService s, CancellationToken ct) =>
            TypedResults.Ok(new { items = await s.AiUsageAsync(user_id, ct) }));
    }

    private static AdminUserPageResponse ToPage(Page<AdminUserDto> p) => new(p.Items, p.PageNumber, p.PageSize, p.Total);

    public sealed record AdminUserPageResponse(IReadOnlyList<AdminUserDto> Items, int Page, int PageSize, int Total);

    public sealed record AdminUserPatch(bool? IsDisabled);
}
