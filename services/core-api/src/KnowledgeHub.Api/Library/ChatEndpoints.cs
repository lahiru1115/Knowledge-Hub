using System.Text;
using System.Text.Json;
using KnowledgeHub.Api.Http;
using KnowledgeHub.Application.Ai;
using KnowledgeHub.Application.Common;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace KnowledgeHub.Api.Library;

/// <summary>Chats and the streamed answer (Doc 02 §8.2, §8.5).</summary>
public static class ChatEndpoints
{
    public static void MapChat(this RouteGroupBuilder api)
    {
        var chat = api.MapGroup("/chat/sessions").RequireAuthorization();
        chat.MapGet("", async (ICurrentUser u, ChatService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListAsync(u.UserId!.Value, ct)));
        chat.MapPost("", async (ChatCreateRequest? body, ICurrentUser u, ChatService s, CancellationToken ct) =>
            TypedResults.Created("/api/v1/chat/sessions", await s.CreateAsync(u.UserId!.Value, body, ct)));
        chat.MapGet("/{id:guid}", async (Guid id, ICurrentUser u, ChatService s, CancellationToken ct) =>
            TypedResults.Ok(await s.GetAsync(u.UserId!.Value, id, ct)));
        chat.MapPatch("/{id:guid}", async (Guid id, ChatRenameRequest body, ICurrentUser u, ChatService s, CancellationToken ct) =>
            TypedResults.Ok(await s.RenameAsync(u.UserId!.Value, id, body, ct)));
        chat.MapDelete("/{id:guid}", async (Guid id, ICurrentUser u, ChatService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(u.UserId!.Value, id, ct);
            return TypedResults.NoContent();
        });
        chat.MapPost("/{id:guid}/messages", Ask);
    }

    private static async Task Ask(
        Guid id, ChatAskRequest body, HttpContext http, ICurrentUser user, ChatService chats, IOptions<KnowledgeHub.Infrastructure.Ai.AiOptions> ai)
    {
        var ct = http.RequestAborted;
        // Problems before the answer starts are thrown here and become ordinary error responses.
        var events = await chats.AskAsync(user.UserId!.Value, id, body, ct);

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";   // tell reverse proxies not to hold the stream back
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await using var enumerator = events.GetAsyncEnumerator(ct);
        var next = enumerator.MoveNextAsync().AsTask();
        while (true)
        {
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wait = Task.Delay(TimeSpan.FromSeconds(ai.Value.HeartbeatSeconds), tick.Token);
            var finished = await Task.WhenAny(next, wait);
            if (finished == wait)
            {
                // A comment line keeps proxies from closing a connection that is quiet while the model thinks.
                await http.Response.WriteAsync(": keep-alive\n\n", Encoding.UTF8, ct);
                await http.Response.Body.FlushAsync(ct);
                continue;
            }
            await tick.CancelAsync();
            if (!await next) break;
            var e = enumerator.Current;
            await http.Response.WriteAsync($"event: {e.Name}\ndata: {e.Data.GetRawText()}\n\n", Encoding.UTF8, ct);
            await http.Response.Body.FlushAsync(ct);
            next = enumerator.MoveNextAsync().AsTask();
        }
    }
}
