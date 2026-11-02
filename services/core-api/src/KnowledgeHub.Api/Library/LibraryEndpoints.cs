using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Library;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace KnowledgeHub.Api.Library;

/// <summary>
/// Library module (Doc 02 §3.2, §8.2): collections, resources, tags. Everything here needs a session; the user's
/// ID comes only from the access token, never from the request.
/// </summary>
public static class LibraryEndpoints
{
    /// <summary>Query string of GET /resources. Names are the contract's snake_case ones.</summary>
    public sealed record ResourceListQuery(
        string? Q,
        [FromQuery(Name = "type")] string[]? Type,
        [FromQuery(Name = "tag")] string[]? Tag,
        [FromQuery(Name = "collection_id")] string? CollectionId,
        bool? Favourite,
        bool? Archived,
        [FromQuery(Name = "created_from")] DateTimeOffset? CreatedFrom,
        [FromQuery(Name = "created_to")] DateTimeOffset? CreatedTo,
        string? Sort,
        int? Page,
        [FromQuery(Name = "page_size")] int? PageSize);

    public static void MapLibrary(this RouteGroupBuilder api)
    {
        var collections = api.MapGroup("/collections").RequireAuthorization();
        collections.MapGet("", async (ICurrentUser u, CollectionService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListAsync(u.UserId!.Value, ct)));
        collections.MapPost("", async (CollectionCreate body, ICurrentUser u, CollectionService s, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(u.UserId!.Value, body, ct);
            return TypedResults.Created($"/api/v1/collections/{created.Id}", created);
        });
        // Registered before /{id} so "order" is never read as an ID.
        collections.MapPut("/order", async (ReorderRequest body, ICurrentUser u, CollectionService s, CancellationToken ct) =>
        {
            await s.ReorderAsync(u.UserId!.Value, body, ct);
            return TypedResults.NoContent();
        });
        collections.MapGet("/{id:guid}", async (Guid id, ICurrentUser u, CollectionService s, CancellationToken ct) =>
            TypedResults.Ok(await s.GetAsync(u.UserId!.Value, id, ct)));
        collections.MapPatch("/{id:guid}", async (Guid id, CollectionUpdate body, ICurrentUser u, CollectionService s, CancellationToken ct) =>
            TypedResults.Ok(await s.UpdateAsync(u.UserId!.Value, id, body, ct)));
        collections.MapDelete("/{id:guid}", async (
            Guid id, string? resources, [FromQuery(Name = "target_collection_id")] Guid? targetCollectionId,
            ICurrentUser u, CollectionService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(u.UserId!.Value, id, ParseMode(resources), targetCollectionId, ct);
            return TypedResults.NoContent();
        });

        var resources = api.MapGroup("/resources").RequireAuthorization();
        resources.MapGet("", async ([AsParameters] ResourceListQuery q, ICurrentUser u, ResourceService s, CancellationToken ct) =>
            TypedResults.Ok(ToPage(await s.ListAsync(u.UserId!.Value,
                new ResourceListRequest(q.Q, q.Type, q.Tag, q.CollectionId, q.Favourite, q.Archived, q.CreatedFrom, q.CreatedTo, q.Sort, q.Page, q.PageSize), ct))));
        resources.MapPost("", async (ResourceCreate body, ICurrentUser u, ResourceService s, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(u.UserId!.Value, body, ct);
            return TypedResults.Created($"/api/v1/resources/{created.Id}", created);
        });
        resources.MapGet("/check-url", async (string? url, ICurrentUser u, ResourceService s, CancellationToken ct) =>
            TypedResults.Ok(await s.CheckUrlAsync(u.UserId!.Value, url, ct)));
        resources.MapPost("/bulk", async (BulkRequest body, ICurrentUser u, ResourceService s, CancellationToken ct) =>
            TypedResults.Ok(await s.BulkAsync(u.UserId!.Value, body, ct)));
        resources.MapGet("/{id:guid}", async (Guid id, ICurrentUser u, ResourceService s, CancellationToken ct) =>
            TypedResults.Ok(await s.GetAsync(u.UserId!.Value, id, ct)));
        resources.MapPatch("/{id:guid}", async (Guid id, ResourceUpdate body, ICurrentUser u, ResourceService s, CancellationToken ct) =>
            TypedResults.Ok(await s.UpdateAsync(u.UserId!.Value, id, body, ct)));
        resources.MapDelete("/{id:guid}", async (Guid id, ICurrentUser u, ResourceService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(u.UserId!.Value, id, ct);
            return TypedResults.NoContent();
        });
        resources.MapPost("/{id:guid}/restore", async (Guid id, ICurrentUser u, ResourceService s, CancellationToken ct) =>
            TypedResults.Ok(await s.RestoreAsync(u.UserId!.Value, id, ct)));

        var tags = api.MapGroup("/tags").RequireAuthorization();
        tags.MapGet("", async (ICurrentUser u, TagService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListAsync(u.UserId!.Value, ct)));
        tags.MapPatch("/{id:guid}", async (Guid id, RenameTagRequest body, ICurrentUser u, TagService s, CancellationToken ct) =>
            TypedResults.Ok(await s.RenameAsync(u.UserId!.Value, id, body, ct)));
        tags.MapPost("/{id:guid}/merge", async (Guid id, MergeTagRequest body, ICurrentUser u, TagService s, CancellationToken ct) =>
            TypedResults.Ok(await s.MergeAsync(u.UserId!.Value, id, body, ct)));
        tags.MapDelete("/{id:guid}", async (Guid id, ICurrentUser u, TagService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(u.UserId!.Value, id, ct);
            return TypedResults.NoContent();
        });
    }

    /// <summary>"delete" or "move" in any case; the framework's own enum binding is case-sensitive.</summary>
    private static DeleteCollectionMode? ParseMode(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (Enum.TryParse<DeleteCollectionMode>(raw, ignoreCase: true, out var mode) && Enum.IsDefined(mode) && !char.IsDigit(raw[0])) return mode;
        throw AppException.Field("resources", "Use delete or move.");
    }

    /// <summary>The list envelope the contract documents: <c>{items, page, page_size, total}</c>.</summary>
    private static ResourcePageResponse ToPage(Page<ResourceDto> p) => new(p.Items, p.PageNumber, p.PageSize, p.Total);

    public sealed record ResourcePageResponse(IReadOnlyList<ResourceDto> Items, int Page, int PageSize, int Total);
}
