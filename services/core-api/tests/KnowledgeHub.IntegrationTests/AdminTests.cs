using System.Net;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class AdminTests(ApiFactory factory)
{
    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<(TestClient Client, string Email)> AdminAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? web = null)
    {
        var client = web is null ? factory.NewClient() : new TestClient(web);
        var email = TestClient.UniqueEmail();
        Assert.Equal(HttpStatusCode.Created, (await client.RegisterAsync(email)).StatusCode);
        await using (var db = factory.Db.Admin())
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync(email)).StatusCode);   // the role is in the token
        return (client, email);
    }

    private async Task<(TestClient Client, string Email, string UserId)> MemberAsync()
    {
        var client = factory.NewClient();
        var email = TestClient.UniqueEmail();
        var user = await (await client.RegisterAsync(email)).Json();
        return (client, email, Id(user));
    }

    // ---------- access ----------

    [Theory]
    [InlineData("GET", "/api/v1/admin/users")]
    [InlineData("PATCH", "/api/v1/admin/users/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/v1/admin/ai-usage")]
    public async Task A_member_is_refused_and_a_visitor_is_not_signed_in(string method, string path)
    {
        var member = await factory.UserAsync();
        var body = method == "PATCH" ? new { is_disabled = true } : null;
        Assert.Equal(HttpStatusCode.Forbidden, (await member.SendAsync(new HttpMethod(method), path, body)).StatusCode);
        var visitor = factory.NewClient();
        await visitor.FetchCsrfAsync();   // so the CSRF check passes and it is the missing sign-in that is refused
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.SendAsync(new HttpMethod(method), path, body)).StatusCode);
    }

    // ---------- the user list ----------

    [Fact]
    public async Task The_list_shows_every_user_with_the_fields_the_screen_needs()
    {
        var (admin, adminEmail) = await AdminAsync();
        var (member, email, id) = await MemberAsync();
        var kept = await member.CreateResourceAsync();
        var gone = await member.CreateResourceAsync();
        await member.SendAsync(HttpMethod.Delete, "/api/v1/resources/" + Id(gone)).ExpectAsync(HttpStatusCode.NoContent);

        var page = await admin.GetAsync("/api/v1/admin/users?q=" + Uri.EscapeDataString(email)).ExpectAsync(HttpStatusCode.OK);

        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal((id, email, "member", 1, false), (Id(row), row.GetProperty("email").GetString(), row.GetProperty("role").GetString()!, row.GetProperty("resource_count").GetInt32(), row.GetProperty("is_disabled").GetBoolean()));
        foreach (var field in new[] { "display_name", "created_at", "last_login_at" }) Assert.True(row.TryGetProperty(field, out _), field);
        Assert.Equal((1, 20, 1), (page.GetProperty("page").GetInt32(), page.GetProperty("page_size").GetInt32(), page.GetProperty("total").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("last_login_at").ValueKind);
        var admins = await admin.GetAsync("/api/v1/admin/users?q=" + Uri.EscapeDataString(adminEmail)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("admin", admins.GetProperty("items")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Search_ignores_case_and_treats_wildcards_literally()
    {
        var (admin, _) = await AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];
        var a = factory.NewClient();
        await a.RegisterAsync($"Find.{tag}@example.com");
        var b = factory.NewClient();
        await b.RegisterAsync($"find_{tag}@example.com");

        var upper = await admin.GetAsync($"/api/v1/admin/users?q={tag.ToUpperInvariant()}").ExpectAsync(HttpStatusCode.OK);
        var underscore = await admin.GetAsync($"/api/v1/admin/users?q=find_{tag}").ExpectAsync(HttpStatusCode.OK);   // _ must not match "."
        var percent = await admin.GetAsync("/api/v1/admin/users?q=%25").ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(2, upper.GetProperty("total").GetInt32());
        Assert.Equal([$"find_{tag}@example.com"], underscore.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("email").GetString()));
        Assert.Equal(0, percent.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_list_is_paged_newest_first_with_a_total()
    {
        var (admin, _) = await AdminAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];
        for (var i = 0; i < 5; i++) await factory.NewClient().RegisterAsync($"page{i}.{tag}@example.com");

        var first = await admin.GetAsync($"/api/v1/admin/users?q={tag}&page_size=2").ExpectAsync(HttpStatusCode.OK);
        var third = await admin.GetAsync($"/api/v1/admin/users?q={tag}&page_size=2&page=3").ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(5, first.GetProperty("total").GetInt32());
        Assert.Equal([$"page4.{tag}@example.com", $"page3.{tag}@example.com"], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("email").GetString()));
        Assert.Equal([$"page0.{tag}@example.com"], third.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("email").GetString()));
    }

    [Theory]
    [InlineData("page=0", "page")]
    [InlineData("page_size=0", "page_size")]
    [InlineData("page_size=101", "page_size")]
    public async Task Bad_paging_is_a_validation_error(string query, string field)
    {
        var (admin, _) = await AdminAsync();
        var response = await admin.GetAsync("/api/v1/admin/users?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Json()).GetProperty("errors").TryGetProperty(field, out _));
    }

    // ---------- disabling ----------

    [Fact]
    public async Task Disabling_a_user_ends_their_sessions_and_blocks_sign_in_until_enabled_again()
    {
        var (admin, _) = await AdminAsync();
        var (member, email, id) = await MemberAsync();
        var refresh = member.LastRefreshCookie!;
        await member.GetAsync("/api/v1/me").ExpectAsync(HttpStatusCode.OK);

        var updated = await admin.SendAsync(HttpMethod.Patch, $"/api/v1/admin/users/{id}", new { is_disabled = true }).ExpectAsync(HttpStatusCode.OK);

        Assert.True(updated.GetProperty("is_disabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/v1/me")).StatusCode);         // at once, not after the cache expires
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.ReplayRefreshAsync(refresh)).StatusCode);
        var login = await factory.NewClient().LoginAsync(email);
        Assert.Equal("account_disabled", (await login.Json()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);

        var enabled = await admin.SendAsync(HttpMethod.Patch, $"/api/v1/admin/users/{id}", new { is_disabled = false }).ExpectAsync(HttpStatusCode.OK);
        Assert.False(enabled.GetProperty("is_disabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient().LoginAsync(email)).StatusCode);
        // Enabling again does not bring the old sessions back.
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.ReplayRefreshAsync(refresh)).StatusCode);
    }

    [Fact]
    public async Task Their_data_is_kept_while_disabled()
    {
        var (admin, _) = await AdminAsync();
        var (member, email, id) = await MemberAsync();
        await member.CreateResourceAsync();
        await admin.SendAsync(HttpMethod.Patch, $"/api/v1/admin/users/{id}", new { is_disabled = true }).ExpectAsync(HttpStatusCode.OK);

        var row = (await admin.GetAsync("/api/v1/admin/users?q=" + Uri.EscapeDataString(email)).ExpectAsync(HttpStatusCode.OK)).GetProperty("items")[0];

        Assert.Equal(1, row.GetProperty("resource_count").GetInt32());
    }

    [Fact]
    public async Task An_admin_cannot_disable_themselves()
    {
        var (admin, email) = await AdminAsync();
        var me = await admin.GetAsync("/api/v1/me").ExpectAsync(HttpStatusCode.OK);

        var response = await admin.SendAsync(HttpMethod.Patch, $"/api/v1/admin/users/{Id(me)}", new { is_disabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Json()).GetProperty("errors").TryGetProperty("is_disabled", out _));
        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient().LoginAsync(email)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_user_is_not_found_and_a_missing_flag_is_invalid()
    {
        var (admin, _) = await AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Patch, $"/api/v1/admin/users/{Guid.NewGuid()}", new { is_disabled = true })).StatusCode);
        var (_, _, id) = await MemberAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Patch, $"/api/v1/admin/users/{id}", new { })).StatusCode);
    }

    [Fact]
    public async Task Disabling_and_enabling_are_audited_once_each_and_repeating_changes_nothing()
    {
        var (admin, adminEmail) = await AdminAsync();
        var (_, _, id) = await MemberAsync();
        foreach (var flag in new[] { true, true, false, false })
            await admin.SendAsync(HttpMethod.Patch, $"/api/v1/admin/users/{id}", new { is_disabled = flag }).ExpectAsync(HttpStatusCode.OK);

        await using var db = factory.Db.Admin();
        var adminId = await db.Users.Where(u => u.Email == adminEmail).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken);
        var events = await db.AuditEvents.AsNoTracking().Where(e => e.UserId == adminId && e.Type.StartsWith("admin.")).OrderBy(e => e.CreatedAt).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["admin.user_disabled", "admin.user_enabled"], events.Select(e => e.Type));
        Assert.All(events, e => Assert.Contains(id, e.Data, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- AI usage ----------

    private async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Web, RecordingAi Ai, TestClient Admin)> WithAiAsync()
    {
        var ai = new RecordingAi();
        var web = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IAiClient>(ai)));
        var (admin, _) = await AdminAsync(web);
        return (web, ai, admin);
    }

    [Fact]
    public async Task AI_usage_comes_from_the_ai_service_for_everyone_or_for_one_user()
    {
        var (web, ai, admin) = await WithAiAsync();
        using var _ = web;
        var user = Guid.NewGuid();
        ai.AdminUsageAnswer = who => who is null
            ? [new AiAdminUsageRow(new DateOnly(2026, 11, 2), null, "chat", 4, 900, 300, 0.75m)]
            : [new AiAdminUsageRow(new DateOnly(2026, 11, 2), who, "embed", 2, 50, 0, 0.0001m)];

        var all = await admin.GetAsync("/api/v1/admin/ai-usage").ExpectAsync(HttpStatusCode.OK);
        var one = await admin.GetAsync("/api/v1/admin/ai-usage?user_id=" + user).ExpectAsync(HttpStatusCode.OK);

        var row = all.GetProperty("items")[0];
        Assert.Equal(("2026-11-02", "chat", 4, 900, 300, 0.75m), (row.GetProperty("date").GetString(), row.GetProperty("operation").GetString(), row.GetProperty("calls").GetInt32(), row.GetProperty("tokens_in").GetInt32(), row.GetProperty("tokens_out").GetInt32(), row.GetProperty("cost_usd").GetDecimal()));
        Assert.Equal(user.ToString(), one.GetProperty("items")[0].GetProperty("user_id").GetString());
        Assert.Equal([(Guid?)null, user], ai.AdminUsageCalls.ToArray());
    }

    [Fact]
    public async Task AI_usage_is_a_503_when_the_ai_service_is_down_and_a_400_for_a_bad_user_id()
    {
        var (web, ai, admin) = await WithAiAsync();
        using var _ = web;
        ai.Failure = () => new AiUnavailableException("down");

        var down = await admin.GetAsync("/api/v1/admin/ai-usage");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("ai_unavailable", (await down.Json()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/v1/admin/ai-usage?user_id=nope")).StatusCode);
    }
}
