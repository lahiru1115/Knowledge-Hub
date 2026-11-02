using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace KnowledgeHub.Infrastructure.Ai;

public sealed class AiClient(HttpClient http, ServiceTokenFactory tokens, AiStatusCache statusCache, IOptions<AiOptions> options) : IAiClient
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private AiOptions O => options.Value;

    public Task IndexAsync(Guid userId, Guid resourceId, AiIndexRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"index/{resourceId}", tokens.ForUser(userId),
            JsonContent.Create(new
            {
                title = request.Title,
                description = request.Description,
                notes = request.Notes,
                collection_id = request.CollectionId,
                text = request.Text,
            }, options: s_json), O.IndexTimeoutSeconds, ct);

    public Task DeleteIndexAsync(Guid userId, Guid resourceId, bool forget, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"index/{resourceId}?forget={(forget ? "true" : "false")}", tokens.ForUser(userId),
            null, O.OtherTimeoutSeconds, ct);

    public Task PurgeUserAsync(Guid userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"users/{userId}", tokens.ForSystem("users:purge"), null, O.OtherTimeoutSeconds, ct);

    public async Task<AiSummary> SummarizeAsync(Guid userId, Guid? resourceId, string? title, string? description, string? text, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Post, "summarize", tokens.ForUser(userId),
            JsonContent.Create(new { resource_id = resourceId, title, description, text }, options: s_json), O.SummaryTimeoutSeconds, ct);
        var r = Parse<SummaryResponse>(body);
        return new AiSummary(r.Summary, r.Source, r.Model);
    }

    public async Task<IReadOnlyList<AiTagSuggestion>> SuggestTagsAsync(
        Guid userId, Guid? resourceId, string? title, string? description, string? text,
        IReadOnlyCollection<string> existingTags, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Post, "suggest-tags", tokens.ForUser(userId),
            JsonContent.Create(new { resource_id = resourceId, title, description, text, existing_tags = existingTags }, options: s_json),
            O.TagTimeoutSeconds, ct);
        return [.. Parse<TagsResponse>(body).Items.Select(i => new AiTagSuggestion(i.Name, i.Existing))];
    }

    public async Task<AiUsageInfo> GetUsageAsync(Guid userId, CancellationToken ct)
    {
        var r = Parse<UsageResponse>(await SendAsync(HttpMethod.Get, "usage", tokens.ForUser(userId), null, O.OtherTimeoutSeconds, ct));
        return new AiUsageInfo(r.ResetAt, [.. r.Items.Select(i => new AiUsageItem(i.Operation, i.Used, i.Limit))]);
    }

    public async Task<IReadOnlyList<AiSearchHit>> SearchAsync(Guid userId, string query, int limit, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Post, "search", tokens.ForUser(userId),
            JsonContent.Create(new { query, limit }, options: s_json), O.SearchTimeoutSeconds, ct);
        return [.. Parse<SearchResponse>(body).Items.Select(i => new AiSearchHit(i.ResourceId, i.Snippet, i.Score))];
    }

    public async Task<IReadOnlyList<AiRelatedHit>> RelatedAsync(Guid userId, Guid resourceId, int limit, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Get, $"resources/{resourceId}/related?limit={limit}", tokens.ForUser(userId),
            null, O.SearchTimeoutSeconds, ct);
        return [.. Parse<RelatedResponse>(body).Items.Select(i => new AiRelatedHit(i.ResourceId, i.Score))];
    }

    public Task<AiStatusInfo> GetStatusAsync(CancellationToken ct) => statusCache.GetAsync(async () =>
    {
        try
        {
            // Short timeout and no token: the readiness check is public inside the private network.
            var body = await SendAsync(HttpMethod.Get, "health/ready", null, null, O.StatusTimeoutSeconds, ct);
            var r = Parse<ReadyResponse>(body);
            return new AiStatusInfo(r.Status == "ok", r.Profile ?? O.Profile, r.ChatAvailable);
        }
        catch (AiUnavailableException)
        {
            return new AiStatusInfo(false, O.Profile, false);
        }
    }, ct);

    private static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, s_json) ?? throw new InvalidOperationException("The AI service sent an empty answer.");

    private async Task<string> SendAsync(HttpMethod method, string path, string? token, HttpContent? content, int timeoutSeconds, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode) return body;
            var status = (int)response.StatusCode;
            if (status >= 500) throw new AiUnavailableException($"The AI service answered {status}.");
            if (TryRejection(status, body) is { } rejected) throw rejected;
            // Any other 4xx means this request is wrong (or the shared secret is): retrying cannot fix it, but it must be visible.
            throw new InvalidOperationException($"The AI service refused {method} {path}: {status} {body}");
        }
        catch (BrokenCircuitException e)
        {
            throw new AiUnavailableException("The AI service is not answering (circuit open).", e);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutRejectedException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AiUnavailableException("The AI service did not answer.", e);
        }
    }

    /// <summary>The two refusals the AI service sends on purpose; both are answers, not failures.</summary>
    private static AiRejectedException? TryRejection(int status, string body)
    {
        if (status is not (422 or 429)) return null;
        try
        {
            var p = JsonSerializer.Deserialize<ProblemBody>(body, s_json);
            return p?.Code switch
            {
                "not_enough_content" => new AiRejectedException(ProblemCode.NotEnoughContent),
                "ai_quota_exceeded" => new AiRejectedException(ProblemCode.AiQuotaExceeded, p.ResetAt, p.RetryAfter),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ProblemBody(string? Code, DateTimeOffset? ResetAt, int? RetryAfter);
    private sealed record SearchItem(Guid ResourceId, string Snippet, double Score);
    private sealed record SearchResponse(List<SearchItem> Items);
    private sealed record RelatedItem(Guid ResourceId, double Score);
    private sealed record RelatedResponse(List<RelatedItem> Items);
    private sealed record SummaryResponse(string Summary, string Source, string Model);
    private sealed record TagItem(string Name, bool Existing);
    private sealed record TagsResponse(List<TagItem> Items);
    private sealed record UsageItem(string Operation, int Used, int? Limit);
    private sealed record UsageResponse(DateTimeOffset ResetAt, List<UsageItem> Items);
    private sealed record ReadyResponse(string Status, string? Profile, bool ChatAvailable);
}

/// <summary>Remembers the AI service's status for a few seconds so the UI can poll it cheaply (Doc 02 §5.2).</summary>
public sealed class AiStatusCache(IOptions<AiOptions> options, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AiStatusInfo? _value;
    private DateTimeOffset _expires;

    public async Task<AiStatusInfo> GetAsync(Func<Task<AiStatusInfo>> load, CancellationToken ct)
    {
        if (_value is not null && time.GetUtcNow() < _expires) return _value;
        await _gate.WaitAsync(ct);
        try
        {
            if (_value is not null && time.GetUtcNow() < _expires) return _value;
            _value = await load();
            _expires = time.GetUtcNow().AddSeconds(options.Value.StatusCacheSeconds);
            return _value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>Used when the AI service is switched off: indexing events complete without doing anything.</summary>
public sealed class DisabledAiClient : IAiClient
{
    public Task IndexAsync(Guid userId, Guid resourceId, AiIndexRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteIndexAsync(Guid userId, Guid resourceId, bool forget, CancellationToken ct) => Task.CompletedTask;
    public Task PurgeUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;

    public Task<AiSummary> SummarizeAsync(Guid userId, Guid? resourceId, string? title, string? description, string? text, CancellationToken ct) =>
        throw new AiUnavailableException("AI features are switched off.");

    public Task<IReadOnlyList<AiTagSuggestion>> SuggestTagsAsync(
        Guid userId, Guid? resourceId, string? title, string? description, string? text,
        IReadOnlyCollection<string> existingTags, CancellationToken ct) =>
        throw new AiUnavailableException("AI features are switched off.");

    public Task<AiUsageInfo> GetUsageAsync(Guid userId, CancellationToken ct) =>
        throw new AiUnavailableException("AI features are switched off.");

    public Task<IReadOnlyList<AiSearchHit>> SearchAsync(Guid userId, string query, int limit, CancellationToken ct) =>
        throw new AiUnavailableException("AI features are switched off.");

    public Task<IReadOnlyList<AiRelatedHit>> RelatedAsync(Guid userId, Guid resourceId, int limit, CancellationToken ct) =>
        throw new AiUnavailableException("AI features are switched off.");

    public Task<AiStatusInfo> GetStatusAsync(CancellationToken ct) => Task.FromResult(new AiStatusInfo(false, "free", false));
}
