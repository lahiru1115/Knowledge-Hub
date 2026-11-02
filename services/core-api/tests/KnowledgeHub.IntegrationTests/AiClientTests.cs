using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Infrastructure.Ai;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>The AI client against a small local server standing in for the AI service. No database needed.</summary>
public sealed class AiClientTests : IAsyncLifetime
{
    private const string Secret = "integration-service-secret-0123456789abcdef";

    private sealed record Seen(string Method, string Path, string Query, string? Auth, string Body);

    private WebApplication _server = null!;
    private readonly ConcurrentQueue<Seen> _seen = new();
    private Func<Seen, int, IResult> _respond = (_, _) => Results.NoContent();
    private int _calls;

    public async ValueTask InitializeAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        _server = b.Build();
        _server.Map("/{**path}", async (HttpContext c) =>
        {
            using var reader = new StreamReader(c.Request.Body, Encoding.UTF8);
            var seen = new Seen(c.Request.Method, c.Request.Path.Value!, c.Request.QueryString.Value ?? "",
                c.Request.Headers.Authorization.ToString(), await reader.ReadToEndAsync());
            _seen.Enqueue(seen);
            var n = Interlocked.Increment(ref _calls);
            return _respond(seen, n);
        });
        await _server.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private ServiceProvider Build(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AiService:Enabled"] = "true",
            ["AiService:BaseUrl"] = _server.Urls.First(),
            ["AiService:JwtSecret"] = Secret,
            ["AiService:RetryBaseDelaySeconds"] = "0.01",
            ["AiService:BreakSeconds"] = "30",
        };
        foreach (var (k, v) in extra ?? []) settings[$"AiService:{k}"] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiClient(config);
        return services.BuildServiceProvider();
    }

    private static async Task<JsonWebToken> ValidateAsync(string header)
    {
        var token = header["Bearer ".Length..];
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "core-api",
            ValidAudience = "ai-service",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.Zero,
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        return (JsonWebToken)result.SecurityToken;
    }

    [Fact]
    public async Task Indexing_sends_a_put_with_the_resource_and_a_token_for_that_user()
    {
        using var sp = Build();
        var user = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var collection = Guid.NewGuid();

        await sp.GetRequiredService<IAiClient>().IndexAsync(user, resource,
            new AiIndexRequest("Title", "Desc", "Notes", collection, "Body text"), TestContext.Current.CancellationToken);

        var seen = Assert.Single(_seen);
        Assert.Equal("PUT", seen.Method);
        Assert.Equal($"/index/{resource}", seen.Path);
        var body = JsonDocument.Parse(seen.Body).RootElement;
        Assert.Equal("Title", body.GetProperty("title").GetString());
        Assert.Equal("Desc", body.GetProperty("description").GetString());
        Assert.Equal("Notes", body.GetProperty("notes").GetString());
        Assert.Equal(collection, body.GetProperty("collection_id").GetGuid());
        Assert.Equal("Body text", body.GetProperty("text").GetString());

        var jwt = await ValidateAsync(seen.Auth!);
        Assert.Equal(user.ToString(), jwt.Subject);
        Assert.False(string.IsNullOrEmpty(jwt.Id));
        Assert.InRange((jwt.ValidTo - jwt.ValidFrom).TotalSeconds, 1, 61);   // short-lived
        Assert.False(jwt.TryGetPayloadValue<string>("scope", out _));         // a user token has no scope
    }

    [Fact]
    public async Task A_missing_text_is_sent_as_null_so_the_stored_text_is_kept()
    {
        using var sp = Build();
        await sp.GetRequiredService<IAiClient>().IndexAsync(Guid.NewGuid(), Guid.NewGuid(),
            new AiIndexRequest("T", null, null, null, null), TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(_seen.Single().Body).RootElement.GetProperty("text").ValueKind);
    }

    [Fact]
    public async Task Delete_and_purge_use_the_right_paths_and_purge_uses_a_scoped_system_token()
    {
        using var sp = Build();
        var ai = sp.GetRequiredService<IAiClient>();
        var user = Guid.NewGuid();
        var resource = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        await ai.DeleteIndexAsync(user, resource, forget: false, ct);
        await ai.DeleteIndexAsync(user, resource, forget: true, ct);
        await ai.PurgeUserAsync(user, ct);

        var calls = _seen.ToList();
        Assert.Equal(("DELETE", $"/index/{resource}", "?forget=false"), (calls[0].Method, calls[0].Path, calls[0].Query));
        Assert.Equal("?forget=true", calls[1].Query);
        Assert.Equal(("DELETE", $"/users/{user}"), (calls[2].Method, calls[2].Path));
        Assert.Equal(user.ToString(), (await ValidateAsync(calls[0].Auth!)).Subject);
        var purge = await ValidateAsync(calls[2].Auth!);
        Assert.Equal("system", purge.Subject);
        Assert.Equal("users:purge", purge.GetPayloadValue<string>("scope"));
    }

    [Fact]
    public async Task A_server_error_is_retried_twice_and_then_succeeds()
    {
        _respond = (_, n) => n < 3 ? Results.StatusCode(503) : Results.NoContent();
        using var sp = Build();
        await sp.GetRequiredService<IAiClient>().DeleteIndexAsync(Guid.NewGuid(), Guid.NewGuid(), false, TestContext.Current.CancellationToken);
        Assert.Equal(3, _seen.Count);
    }

    [Fact]
    public async Task Three_server_errors_in_a_row_become_ai_unavailable()
    {
        _respond = (_, _) => Results.StatusCode(500);
        using var sp = Build();
        await Assert.ThrowsAsync<AiUnavailableException>(() =>
            sp.GetRequiredService<IAiClient>().DeleteIndexAsync(Guid.NewGuid(), Guid.NewGuid(), false, TestContext.Current.CancellationToken));
        Assert.Equal(3, _seen.Count);   // the call and two retries
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_client_error_is_not_retried_and_is_not_reported_as_unavailable(int status)
    {
        _respond = (_, _) => Results.StatusCode(status);
        using var sp = Build();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sp.GetRequiredService<IAiClient>().PurgeUserAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Message, StringComparison.Ordinal);
        Assert.Single(_seen);
    }

    [Fact]
    public async Task The_circuit_opens_after_repeated_failures_and_then_calls_fail_without_reaching_the_server()
    {
        _respond = (_, _) => Results.StatusCode(500);
        using var sp = Build(new() { ["Retries"] = "0", ["BreakAfterFailures"] = "5" });
        var ai = sp.GetRequiredService<IAiClient>();
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<AiUnavailableException>(() => ai.DeleteIndexAsync(Guid.NewGuid(), Guid.NewGuid(), false, ct));
        Assert.Equal(5, _seen.Count);

        var e = await Assert.ThrowsAsync<AiUnavailableException>(() => ai.DeleteIndexAsync(Guid.NewGuid(), Guid.NewGuid(), false, ct));
        Assert.Contains("circuit", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, _seen.Count);   // nothing more was sent
    }

    [Fact]
    public async Task A_slow_service_times_out_as_ai_unavailable()
    {
        _respond = (_, _) => { Thread.Sleep(2500); return Results.NoContent(); };
        using var sp = Build(new() { ["OtherTimeoutSeconds"] = "1", ["Retries"] = "0" });
        await Assert.ThrowsAsync<AiUnavailableException>(() =>
            sp.GetRequiredService<IAiClient>().DeleteIndexAsync(Guid.NewGuid(), Guid.NewGuid(), false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_service_that_is_not_running_is_ai_unavailable()
    {
        using var sp = Build(new() { ["BaseUrl"] = "http://127.0.0.1:1", ["Retries"] = "0" });
        await Assert.ThrowsAsync<AiUnavailableException>(() =>
            sp.GetRequiredService<IAiClient>().PurgeUserAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_turned_into_unavailable()
    {
        _respond = (_, _) => { Thread.Sleep(1000); return Results.NoContent(); };
        using var sp = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sp.GetRequiredService<IAiClient>().PurgeUserAsync(Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task When_the_service_is_switched_off_nothing_is_called()
    {
        using var sp = Build(new() { ["Enabled"] = "false" });
        var ai = sp.GetRequiredService<IAiClient>();
        Assert.IsType<DisabledAiClient>(ai);
        await ai.PurgeUserAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Empty(_seen);
    }

    [Fact]
    public void A_short_secret_is_refused_when_the_service_is_switched_on()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build(new() { ["JwtSecret"] = "too-short" }));
        Assert.Contains("32 bytes", e.Message, StringComparison.Ordinal);
    }

    // ---------- the user-facing calls ----------

    [Fact]
    public async Task Summarise_sends_the_resource_and_reads_the_answer()
    {
        _respond = (_, _) => Results.Json(new { summary = "Short.", source = "extracted", model = "extractive-v1" });
        using var sp = Build();
        var user = Guid.NewGuid();
        var resource = Guid.NewGuid();

        var s = await sp.GetRequiredService<IAiClient>().SummarizeAsync(user, resource, "T", "D", null, TestContext.Current.CancellationToken);

        Assert.Equal(new AiSummary("Short.", "extracted", "extractive-v1"), s);
        var seen = Assert.Single(_seen);
        Assert.Equal(("POST", "/summarize"), (seen.Method, seen.Path));
        var body = JsonDocument.Parse(seen.Body).RootElement;
        Assert.Equal(resource, body.GetProperty("resource_id").GetGuid());
        Assert.Equal(user.ToString(), (await ValidateAsync(seen.Auth!)).Subject);
    }

    [Fact]
    public async Task Tag_suggestions_send_the_existing_tags_and_read_the_items()
    {
        _respond = (_, _) => Results.Json(new { items = new[] { new { name = "pooling", existing = true }, new { name = "npgsql", existing = false } } });
        using var sp = Build();
        var items = await sp.GetRequiredService<IAiClient>().SuggestTagsAsync(Guid.NewGuid(), null, "T", null, "text", ["pooling", "db"], TestContext.Current.CancellationToken);
        Assert.Equal([new AiTagSuggestion("pooling", true), new AiTagSuggestion("npgsql", false)], items);
        Assert.Equal(["pooling", "db"], JsonDocument.Parse(_seen.Single().Body).RootElement.GetProperty("existing_tags").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Usage_is_read_with_null_limits()
    {
        _respond = (_, _) => Results.Json(new
        {
            reset_at = "2030-01-02T00:00:00+00:00",
            items = new object[] { new { operation = "summarize", used = 3, limit = 300 }, new { operation = "search", used = 0, limit = (int?)null } },
        });
        using var sp = Build();
        var u = await sp.GetRequiredService<IAiClient>().GetUsageAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 0, 0, 0, TimeSpan.Zero), u.ResetAt);
        Assert.Equal([new AiUsageItem("summarize", 3, 300), new AiUsageItem("search", 0, null)], u.Items);
    }

    [Fact]
    public async Task The_ais_two_deliberate_refusals_become_rejections_and_are_not_retried()
    {
        _respond = (_, _) => Results.Json(new { code = "ai_quota_exceeded", reset_at = "2030-01-02T00:00:00+00:00", retry_after = 600 }, statusCode: 429);
        using var sp = Build();
        var ai = sp.GetRequiredService<IAiClient>();
        var quota = await Assert.ThrowsAsync<AiRejectedException>(() => ai.GetUsageAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Equal((ProblemCode.AiQuotaExceeded, 600), (quota.Code, quota.RetryAfterSeconds));
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 0, 0, 0, TimeSpan.Zero), quota.ResetAt);
        Assert.Single(_seen);   // a GET, so it could be retried, but a 429 is an answer

        _respond = (_, _) => Results.Json(new { code = "not_enough_content" }, statusCode: 422);
        var thin = await Assert.ThrowsAsync<AiRejectedException>(() => ai.SummarizeAsync(Guid.NewGuid(), null, "T", null, null, TestContext.Current.CancellationToken));
        Assert.Equal(ProblemCode.NotEnoughContent, thin.Code);
    }

    [Fact]
    public async Task One_users_quota_answers_never_open_the_circuit_for_everyone()
    {
        _respond = (_, _) => Results.Json(new { code = "ai_quota_exceeded", retry_after = 60 }, statusCode: 429);
        using var sp = Build(new() { ["Retries"] = "0", ["BreakAfterFailures"] = "5" });
        var ai = sp.GetRequiredService<IAiClient>();
        for (var i = 0; i < 12; i++)
            await Assert.ThrowsAsync<AiRejectedException>(() => ai.GetUsageAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        _respond = (_, _) => Results.Json(new { reset_at = "2030-01-02T00:00:00+00:00", items = Array.Empty<object>() });
        await ai.GetUsageAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);   // still reaches the service
        Assert.Equal(13, _seen.Count);
    }

    [Fact]
    public async Task A_post_is_never_repeated_even_when_the_service_fails_but_a_get_is()
    {
        _respond = (_, _) => Results.StatusCode(503);
        using var sp = Build();
        var ai = sp.GetRequiredService<IAiClient>();
        await Assert.ThrowsAsync<AiUnavailableException>(() => ai.SummarizeAsync(Guid.NewGuid(), null, "T", null, null, TestContext.Current.CancellationToken));
        Assert.Single(_seen);   // summarising may cost quota, so it is not repeated blindly

        while (_seen.TryDequeue(out _)) { }
        await Assert.ThrowsAsync<AiUnavailableException>(() => ai.GetUsageAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Equal(3, _seen.Count);
    }

    [Fact]
    public async Task Status_is_remembered_for_a_few_seconds_and_a_dead_service_is_just_unavailable()
    {
        _respond = (_, _) => Results.Json(new { status = "ok", profile = "claude", chat_available = true });
        using var sp = Build();
        var ai = sp.GetRequiredService<IAiClient>();
        var first = await ai.GetStatusAsync(TestContext.Current.CancellationToken);
        await ai.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new AiStatusInfo(true, "claude", true), first);
        Assert.Single(_seen);   // the second call came from the cache
        Assert.True(string.IsNullOrEmpty(_seen.Single().Auth));   // the readiness check carries no token

        using var down = Build(new() { ["BaseUrl"] = "http://127.0.0.1:1", ["Retries"] = "0", ["Profile"] = "claude" });
        var status = await down.GetRequiredService<IAiClient>().GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new AiStatusInfo(false, "claude", false), status);
    }

    [Fact]
    public async Task A_service_that_says_it_is_not_ready_is_reported_unavailable()
    {
        _respond = (_, _) => Results.Json(new { status = "not_migrated" }, statusCode: 503);
        using var sp = Build(new() { ["Retries"] = "0" });
        Assert.False((await sp.GetRequiredService<IAiClient>().GetStatusAsync(TestContext.Current.CancellationToken)).Available);
    }
}
