using System.Net;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class ChatTests(ApiFactory factory)
{
    private async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Web, RecordingAi Ai, TestClient Client)> ArrangeAsync(Action<IWebHostBuilder>? configure = null)
    {
        var ai = new RecordingAi();
        var web = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Outbox:Enabled", "false");
            b.ConfigureServices(s => s.AddSingleton<IAiClient>(ai));
            configure?.Invoke(b);
        });
        var client = new TestClient(web);
        Assert.Equal(HttpStatusCode.Created, (await client.RegisterAsync(TestClient.UniqueEmail())).StatusCode);
        return (web, ai, client);
    }

    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private static List<(string Name, JsonElement Data)> Parse(string body)
    {
        var events = new List<(string, JsonElement)>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (block.StartsWith(':')) continue;
            var lines = block.Split('\n');
            var name = lines.First(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            events.Add((name, JsonDocument.Parse(data).RootElement.Clone()));
        }
        return events;
    }

    private static IReadOnlyList<AiChatEvent> Answer(Guid resource, string text = "Pooling keeps connections open [1].") =>
    [
        RecordingAi.Event("token", new { text = text[..8] }),
        RecordingAi.Event("token", new { text = text[8..] }),
        RecordingAi.Event("citations", new { items = new[] { new { n = 1, resource_id = resource, title = "stale title", quote = "a quote" } } }),
        RecordingAi.Event("done", new { message_id = Guid.NewGuid(), usage = new { tokens_in = 10, tokens_out = 5 } }),
    ];

    // ---------- sessions ----------

    [Fact]
    public async Task A_chat_can_be_started_listed_renamed_and_deleted()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var created = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("scope_collection_id").ValueKind);

        var listed = await c.GetAsync("/api/v1/chat/sessions").ExpectAsync(HttpStatusCode.OK);
        Assert.Equal([Id(created)], listed.GetProperty("items").EnumerateArray().Select(Id));

        var renamed = await c.PatchAsync($"/api/v1/chat/sessions/{Id(created)}", new { title = "  Cars  " }).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Cars", renamed.GetProperty("title").GetString());
        await c.PatchAsync($"/api/v1/chat/sessions/{Id(created)}", new { title = "" }).ExpectAsync(HttpStatusCode.BadRequest);
        await c.PatchAsync($"/api/v1/chat/sessions/{Id(created)}", new { title = new string('x', 201) }).ExpectAsync(HttpStatusCode.BadRequest);

        await c.SendAsync(HttpMethod.Delete, $"/api/v1/chat/sessions/{Id(created)}").ExpectAsync(HttpStatusCode.NoContent);
        await c.GetAsync($"/api/v1/chat/sessions/{Id(created)}").ExpectAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_chat_can_be_limited_to_one_of_your_collections_but_not_someone_elses()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var mine = await c.CreateCollectionAsync("Cars");
        var theirs = await (await factory.UserAsync()).CreateCollectionAsync("Secret");

        var s = await c.PostAsync("/api/v1/chat/sessions", new { scope_collection_id = Id(mine) }).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(Id(mine), s.GetProperty("scope_collection_id").GetString());
        await c.PostAsync("/api/v1/chat/sessions", new { scope_collection_id = Id(theirs) }).ExpectAsync(HttpStatusCode.NotFound);
        await c.PostAsync("/api/v1/chat/sessions", new { scope_collection_id = Guid.NewGuid() }).ExpectAsync(HttpStatusCode.NotFound);
        Assert.Single(ai.Sessions);   // the refused ones never reached the AI service
    }

    [Fact]
    public async Task Another_users_chat_is_not_found_for_every_operation()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        var other = new TestClient(web);
        await other.RegisterAsync(TestClient.UniqueEmail());
        var path = $"/api/v1/chat/sessions/{Id(s)}";
        await other.GetAsync(path).ExpectAsync(HttpStatusCode.NotFound);
        await other.PatchAsync(path, new { title = "x" }).ExpectAsync(HttpStatusCode.NotFound);
        await other.SendAsync(HttpMethod.Delete, path).ExpectAsync(HttpStatusCode.NotFound);
        await other.PostAsync(path + "/messages", new { content = "hi" }).ExpectAsync(HttpStatusCode.NotFound);
        Assert.Empty(ai.Questions);
        Assert.Empty((await other.GetAsync("/api/v1/chat/sessions").ExpectAsync(HttpStatusCode.OK)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Reading_a_chat_shows_the_current_title_and_link_of_each_cited_resource_and_hides_ones_that_are_gone()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var kept = await c.CreateResourceAsync("https://example.com/kept", new { title = "Kept page" });
        var removed = await c.CreateResourceAsync("https://example.com/removed", new { title = "Removed page" });
        var foreign = await (await factory.UserAsync()).CreateResourceAsync();
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        var session = ai.Sessions[Guid.Parse(Id(s))];
        session.Messages.Add(new AiChatMessage(Guid.NewGuid(), "assistant", "Answer [1][2][3]",
            [new(1, Guid.Parse(Id(kept)), "old title", "q1"), new(2, Guid.Parse(Id(removed)), "x", "q2"), new(3, Guid.Parse(Id(foreign)), "y", "q3")],
            DateTimeOffset.UtcNow));
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(removed)}").ExpectAsync(HttpStatusCode.NoContent);

        var detail = await c.GetAsync($"/api/v1/chat/sessions/{Id(s)}").ExpectAsync(HttpStatusCode.OK);

        var cites = detail.GetProperty("messages")[0].GetProperty("citations").EnumerateArray().ToList();
        Assert.Single(cites);
        Assert.Equal(("Kept page", "https://example.com/kept", "q1", 1), (cites[0].GetProperty("title").GetString(), cites[0].GetProperty("url").GetString(), cites[0].GetProperty("quote").GetString(), cites[0].GetProperty("n").GetInt32()));
        Assert.Equal("Answer [1][2][3]", detail.GetProperty("messages")[0].GetProperty("content").GetString());   // the text is untouched
    }

    // ---------- the answer ----------

    [Fact]
    public async Task The_answer_streams_through_with_the_right_headers_and_hydrated_citations()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var res = await c.CreateResourceAsync("https://example.com/pool", new { title = "Pooling guide" });
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        ai.AnswerEvents = () => Answer(Guid.Parse(Id(res)));

        var response = await c.PostAsync($"/api/v1/chat/sessions/{Id(s)}/messages", new { content = "  How does pooling work?  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl!.ToString());
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());
        var events = Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["token", "token", "citations", "done"], events.Select(e => e.Name));
        Assert.Equal("Pooling keeps connections open [1].", string.Concat(events.Where(e => e.Name == "token").Select(e => e.Data.GetProperty("text").GetString())));
        var cite = events[2].Data.GetProperty("items")[0];
        Assert.Equal(("Pooling guide", "https://example.com/pool", "a quote"), (cite.GetProperty("title").GetString(), cite.GetProperty("url").GetString(), cite.GetProperty("quote").GetString()));
        Assert.Equal(15, events[3].Data.GetProperty("usage").GetProperty("tokens_in").GetInt32() + events[3].Data.GetProperty("usage").GetProperty("tokens_out").GetInt32());
        Assert.Equal("How does pooling work?", ai.Questions.Single().Content);   // trimmed
        Assert.True(ai.Disposed);
    }

    [Fact]
    public async Task A_citation_to_something_that_is_not_yours_is_removed_from_the_stream()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var foreign = await (await factory.UserAsync()).CreateResourceAsync();
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        ai.AnswerEvents = () => Answer(Guid.Parse(Id(foreign)));
        var response = await c.PostAsync($"/api/v1/chat/sessions/{Id(s)}/messages", new { content = "q" });
        var events = Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(events.Single(e => e.Name == "citations").Data.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(Id(foreign), await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_part_way_is_passed_on_as_an_event_and_the_connection_is_closed()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        ai.AnswerEvents = () => [RecordingAi.Event("token", new { text = "Partial " }), RecordingAi.Event("error", new { code = "ai_unavailable" })];
        var response = await c.PostAsync($"/api/v1/chat/sessions/{Id(s)}/messages", new { content = "q" });
        var events = Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["token", "error"], events.Select(e => e.Name));
        Assert.Equal("ai_unavailable", events[1].Data.GetProperty("code").GetString());
        Assert.True(ai.Disposed);
    }

    [Fact]
    public async Task A_quiet_stream_gets_comment_lines_so_proxies_keep_it_open()
    {
        var (web, ai, c) = await ArrangeAsync(b => b.UseSetting("AiService:HeartbeatSeconds", "0.05"));
        using var _w = web;
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        ai.AnswerEvents = () => [RecordingAi.Event("token", new { text = "Slow" }), RecordingAi.Event("done", new { message_id = Guid.NewGuid(), usage = new { tokens_in = 1, tokens_out = 1 } })];
        ai.EventDelay = TimeSpan.FromMilliseconds(400);

        var body = await (await c.PostAsync($"/api/v1/chat/sessions/{Id(s)}/messages", new { content = "q" })).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(body.Split(": keep-alive").Length > 2, body);
        Assert.Equal(["token", "done"], Parse(body).Select(e => e.Name));   // comments are not events
    }

    [Fact]
    public async Task Problems_before_the_answer_starts_are_ordinary_error_responses_not_streams()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        var path = $"/api/v1/chat/sessions/{Id(s)}/messages";

        var reset = DateTimeOffset.UtcNow.Date.AddDays(1);
        ai.ChatOpenFailure = () => new AiRejectedException(ProblemCode.AiQuotaExceeded, reset, 3600);
        var quota = await c.PostAsync(path, new { content = "q" });
        Assert.Equal(HttpStatusCode.TooManyRequests, quota.StatusCode);
        Assert.Equal("application/problem+json", quota.Content.Headers.ContentType!.MediaType);
        var body = await quota.Json();
        Assert.Equal("ai_quota_exceeded", body.GetProperty("code").GetString());
        Assert.Equal(reset, body.GetProperty("reset_at").GetDateTimeOffset());
        Assert.Equal("3600", quota.Headers.GetValues("Retry-After").Single());

        ai.ChatOpenFailure = () => new AiUnavailableException("down");
        var down = await c.PostAsync(path, new { content = "q" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("30", down.Headers.GetValues("Retry-After").Single());
        Assert.Equal("ai_unavailable", (await down.Json()).GetProperty("code").GetString());

        ai.ChatOpenFailure = null;
        await c.PostAsync($"/api/v1/chat/sessions/{Guid.NewGuid()}/messages", new { content = "q" }).ExpectAsync(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_question_is_refused_without_asking_the_ai(string content)
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        await c.PostAsync($"/api/v1/chat/sessions/{Id(s)}/messages", new { content }).ExpectAsync(HttpStatusCode.BadRequest);
        await c.PostAsync($"/api/v1/chat/sessions/{Id(s)}/messages", new { }).ExpectAsync(HttpStatusCode.BadRequest);
        Assert.Empty(ai.Questions);
    }

    [Fact]
    public async Task A_question_may_be_4000_characters_but_not_4001()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        var path = $"/api/v1/chat/sessions/{Id(s)}/messages";
        await c.PostAsync(path, new { content = new string('q', 4001) }).ExpectAsync(HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync(path, new { content = new string('q', 4000) })).StatusCode);
    }

    [Fact]
    public async Task Chat_needs_a_session_and_a_csrf_token()
    {
        var (web, ai, c) = await ArrangeAsync();
        using var _w = web;
        var s = await c.PostAsync("/api/v1/chat/sessions", new { }).ExpectAsync(HttpStatusCode.Created);
        var anon = new TestClient(web);
        await anon.FetchCsrfAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/chat/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync($"/api/v1/chat/sessions/{Id(s)}/messages", new { content = "q" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/v1/chat/sessions/{Id(s)}/messages", new { content = "q" }, csrf: false)).StatusCode);
        Assert.Empty(ai.Questions);
    }
}
