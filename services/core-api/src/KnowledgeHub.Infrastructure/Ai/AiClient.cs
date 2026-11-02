using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace KnowledgeHub.Infrastructure.Ai;

public sealed class AiClient(HttpClient http, ServiceTokenFactory tokens, IOptions<AiOptions> options) : IAiClient
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public Task IndexAsync(Guid userId, Guid resourceId, AiIndexRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"index/{resourceId}", tokens.ForUser(userId),
            JsonContent.Create(new
            {
                title = request.Title,
                description = request.Description,
                notes = request.Notes,
                collection_id = request.CollectionId,
                text = request.Text,
            }, options: s_json), options.Value.IndexTimeoutSeconds, ct);

    public Task DeleteIndexAsync(Guid userId, Guid resourceId, bool forget, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"index/{resourceId}?forget={(forget ? "true" : "false")}", tokens.ForUser(userId),
            null, options.Value.OtherTimeoutSeconds, ct);

    public Task PurgeUserAsync(Guid userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"users/{userId}", tokens.ForSystem("users:purge"),
            null, options.Value.OtherTimeoutSeconds, ct);

    private async Task SendAsync(HttpMethod method, string path, string token, HttpContent? content, int timeoutSeconds, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode) return;
            if ((int)response.StatusCode >= 500)
                throw new AiUnavailableException($"The AI service answered {(int)response.StatusCode}.");
            // 4xx means this request is wrong (or the shared secret is): retrying cannot fix it, but it must be visible.
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"The AI service refused {method} {path}: {(int)response.StatusCode} {detail}");
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
}

/// <summary>Used when the AI service is switched off: indexing events complete without doing anything.</summary>
public sealed class DisabledAiClient : IAiClient
{
    public Task IndexAsync(Guid userId, Guid resourceId, AiIndexRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteIndexAsync(Guid userId, Guid resourceId, bool forget, CancellationToken ct) => Task.CompletedTask;
    public Task PurgeUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
}
