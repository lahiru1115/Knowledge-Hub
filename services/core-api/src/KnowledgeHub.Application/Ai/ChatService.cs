using System.Runtime.CompilerServices;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Ai;

public sealed record ChatSessionDto(Guid Id, string Title, Guid? ScopeCollectionId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ChatListDto(IReadOnlyList<ChatSessionDto> Items);
public sealed record CitationDto(int N, Guid ResourceId, string Title, string Url, string? Quote);
public sealed record ChatMessageDto(Guid Id, string Role, string Content, IReadOnlyList<CitationDto> Citations, DateTimeOffset CreatedAt);
public sealed record ChatSessionDetailDto(
    Guid Id, string Title, Guid? ScopeCollectionId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ChatMessageDto> Messages);
public sealed record ChatCreateRequest(Guid? ScopeCollectionId);
public sealed record ChatRenameRequest(string? Title);
public sealed record ChatAskRequest(string? Content);

/// <summary>One event of an answer on its way to the browser.</summary>
public sealed record ChatEventDto(string Name, JsonElement Data);

/// <summary>
/// "Ask your library" (FR-AI-06, FR-AI-08). Chats live in the AI service; this adds what only the Core API knows:
/// that a scope collection is the user's, and the title and address of each cited resource, checked against what the
/// user still owns, so a citation can never point at someone else's page or a deleted one.
/// </summary>
public sealed class ChatService(IAppDbContext db, IAiClient ai)
{
    public const int MaxQuestionLength = 4000;
    public const int MaxTitleLength = 200;

    public async Task<ChatSessionDto> CreateAsync(Guid userId, ChatCreateRequest? request, CancellationToken ct)
    {
        if (request?.ScopeCollectionId is { } collectionId
            && !await db.Collections.OwnedBy(userId).AnyAsync(c => c.Id == collectionId, ct))
            throw new AppException(ProblemCode.NotFound);
        return ToDto(await AiFeaturesService.CallAsync(() => ai.CreateChatSessionAsync(userId, request?.ScopeCollectionId, ct)));
    }

    public async Task<ChatListDto> ListAsync(Guid userId, CancellationToken ct) =>
        new([.. (await AiFeaturesService.CallAsync(() => ai.ListChatSessionsAsync(userId, ct))).Select(ToDto)]);

    public async Task<ChatSessionDetailDto> GetAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var detail = await AiFeaturesService.CallAsync(() => ai.GetChatSessionAsync(userId, sessionId, ct));
        var known = await ResourcesAsync(userId, detail.Messages.SelectMany(m => m.Citations.Select(c => c.ResourceId)), ct);
        var messages = detail.Messages.Select(m => new ChatMessageDto(
            m.Id, m.Role, m.Content,
            [.. m.Citations.Where(c => known.ContainsKey(c.ResourceId)).Select(c => Cite(c.N, c.ResourceId, c.Quote, known))],
            m.CreatedAt)).ToList();
        var s = detail.Session;
        return new ChatSessionDetailDto(s.Id, s.Title, s.ScopeCollectionId, s.CreatedAt, s.UpdatedAt, messages);
    }

    public async Task<ChatSessionDto> RenameAsync(Guid userId, Guid sessionId, ChatRenameRequest request, CancellationToken ct)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title)) throw AppException.Field("title", "Enter a name.");
        if (title.Length > MaxTitleLength) throw AppException.Field("title", $"Use {MaxTitleLength} characters or fewer.");
        return ToDto(await AiFeaturesService.CallAsync(() => ai.RenameChatSessionAsync(userId, sessionId, title, ct)));
    }

    public Task DeleteAsync(Guid userId, Guid sessionId, CancellationToken ct) =>
        AiFeaturesService.CallAsync(async () =>
        {
            await ai.DeleteChatSessionAsync(userId, sessionId, ct);
            return true;
        });

    /// <summary>
    /// Starts an answer. Anything that goes wrong before it starts (no such chat, over the quota, AI down, a bad
    /// question) is thrown from here, so it becomes a normal error response. The events then stream from the result.
    /// </summary>
    public async Task<IAsyncEnumerable<ChatEventDto>> AskAsync(Guid userId, Guid sessionId, ChatAskRequest request, CancellationToken ct)
    {
        var content = request.Content?.Trim();
        if (string.IsNullOrEmpty(content)) throw AppException.Field("content", "Type a question.");
        if (content.Length > MaxQuestionLength) throw AppException.Field("content", $"Use {MaxQuestionLength:N0} characters or fewer.");
        var answer = await AiFeaturesService.CallAsync(() => ai.OpenChatAsync(userId, sessionId, content, ct));
        return Relay(answer, userId, ct);
    }

    private async IAsyncEnumerable<ChatEventDto> Relay(IAiChatAnswer answer, Guid userId, [EnumeratorCancellation] CancellationToken ct)
    {
        await using (answer)
        {
            await foreach (var e in answer.ReadAsync(ct))
            {
                if (e.Name == "citations") yield return await HydrateAsync(e, userId, ct);
                else yield return new ChatEventDto(e.Name, e.Data);
            }
        }
    }

    /// <summary>Fills in each citation's current title and address, and drops any that is not the user's live resource.</summary>
    private async Task<ChatEventDto> HydrateAsync(AiChatEvent e, Guid userId, CancellationToken ct)
    {
        var raw = new List<(int N, Guid Id, string? Quote)>();
        if (e.Data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var i in items.EnumerateArray())
                if (i.TryGetProperty("n", out var n) && n.TryGetInt32(out var number)
                    && i.TryGetProperty("resource_id", out var r) && r.TryGetGuid(out var id))
                    raw.Add((number, id, i.TryGetProperty("quote", out var q) ? q.GetString() : null));
        var known = await ResourcesAsync(userId, raw.Select(x => x.Id), ct);
        var kept = raw.Where(x => known.ContainsKey(x.Id))
            .Select(x => Cite(x.N, x.Id, x.Quote, known))
            .Select(c => new { n = c.N, resource_id = c.ResourceId, title = c.Title, url = c.Url, quote = c.Quote });
        return new ChatEventDto("citations", JsonSerializer.SerializeToElement(new { items = kept }));
    }

    private async Task<Dictionary<Guid, Resource>> ResourcesAsync(Guid userId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Resources.OwnedBy(userId).AsNoTracking().Where(r => wanted.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
    }

    private static CitationDto Cite(int n, Guid id, string? quote, Dictionary<Guid, Resource> known) =>
        new(n, id, known[id].Title ?? known[id].Url, known[id].Url, quote);

    private static ChatSessionDto ToDto(AiChatSession s) => new(s.Id, s.Title, s.ScopeCollectionId, s.CreatedAt, s.UpdatedAt);
}
