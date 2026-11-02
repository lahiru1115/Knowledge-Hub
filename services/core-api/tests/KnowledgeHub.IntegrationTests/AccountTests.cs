using System.Net;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class AccountTests(ApiFactory factory)
{
    private const string Password = "a long unusual passphrase";
    private const string NewPassword = "another long unusual passphrase";

    private async Task<(TestClient Client, string Email)> SignedInAsync()
    {
        var client = factory.NewClient();
        var email = TestClient.UniqueEmail();
        Assert.Equal(HttpStatusCode.Created, (await client.RegisterAsync(email)).StatusCode);
        return (client, email);
    }

    private static async Task<HttpResponseMessage> RequestResetAsync(TestClient client, string email)
    {
        if (client.Csrf is null) await client.FetchCsrfAsync();
        return await client.PostAsync("/api/v1/auth/password-reset", new { email });
    }

    // ---------- password reset (FR-AUTH-06) ----------

    [Fact]
    public async Task Reset_request_emails_a_single_use_link_valid_for_30_minutes()
    {
        var (_, email) = await SignedInAsync();
        var anonymous = factory.NewClient();

        var response = await RequestResetAsync(anonymous, email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var message = Assert.Single(factory.Emails.To(email));
        Assert.Contains("http://localhost:5173/reset-password?token=", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("30 minutes", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, message.TextBody, StringComparison.Ordinal);

        await using var db = factory.Db.Admin();
        var token = await db.PasswordResetTokens.SingleAsync(t => db.Users.Any(u => u.Id == t.UserId && u.Email == email), TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromMinutes(30), token.ExpiresAt - token.CreatedAt);
    }

    [Fact]
    public async Task Reset_request_gives_the_same_answer_whether_or_not_the_account_exists()
    {
        var (_, email) = await SignedInAsync();
        var unknown = TestClient.UniqueEmail();

        var known = await RequestResetAsync(factory.NewClient(), email);
        var missing = await RequestResetAsync(factory.NewClient(), unknown);

        Assert.Equal(known.StatusCode, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, missing.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(factory.Emails.To(unknown));
    }

    [Fact]
    public async Task No_reset_email_is_sent_for_a_disabled_account()
    {
        var (_, email) = await SignedInAsync();
        await using (var db = factory.Db.Admin())
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, true), TestContext.Current.CancellationToken);

        var response = await RequestResetAsync(factory.NewClient(), email);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(factory.Emails.To(email));
    }

    [Fact]
    public async Task Reset_request_with_an_invalid_email_returns_400()
    {
        var response = await RequestResetAsync(factory.NewClient(), "not-an-email");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reset_tokens_are_stored_only_as_hashes()
    {
        var (_, email) = await SignedInAsync();
        await RequestResetAsync(factory.NewClient(), email);
        var raw = factory.Emails.ResetToken(email);

        await using var db = factory.Db.Admin();
        var hash = await db.PasswordResetTokens.Where(t => db.Users.Any(u => u.Id == t.UserId && u.Email == email))
            .Select(t => t.TokenHash).SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(64, hash.Length);
        Assert.NotEqual(raw, hash);
    }

    [Fact]
    public async Task Confirming_a_reset_sets_the_new_password_and_the_link_cannot_be_used_twice()
    {
        var (_, email) = await SignedInAsync();
        await RequestResetAsync(factory.NewClient(), email);
        var token = factory.Emails.ResetToken(email);
        var client = factory.NewClient();
        await client.FetchCsrfAsync();

        var confirmed = await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token, new_password = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().LoginAsync(email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient().LoginAsync(email, NewPassword)).StatusCode);

        var again = await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token, new_password = "yet another long passphrase" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_token", (await TestClient.ReadAsync(again)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Confirming_a_reset_ends_every_existing_session_at_once()
    {
        var (session, email) = await SignedInAsync();
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/v1/me")).StatusCode); // warms the stamp cache
        var refreshToken = session.LastRefreshCookie!;

        await RequestResetAsync(factory.NewClient(), email);
        var client = factory.NewClient();
        await client.FetchCsrfAsync();
        await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token = factory.Emails.ResetToken(email), new_password = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.ReplayRefreshAsync(refreshToken)).StatusCode);
    }

    [Fact]
    public async Task Resetting_the_password_unlocks_a_locked_account()
    {
        var (_, email) = await SignedInAsync();
        var attacker = factory.NewClient();
        for (var i = 0; i < 10; i++) await attacker.LoginAsync(email, "wrong password " + i);
        Assert.Equal((HttpStatusCode)423, (await attacker.LoginAsync(email, Password)).StatusCode);

        await RequestResetAsync(factory.NewClient(), email);
        var client = factory.NewClient();
        await client.FetchCsrfAsync();
        await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token = factory.Emails.ResetToken(email), new_password = NewPassword });

        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient().LoginAsync(email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task An_expired_or_unknown_link_returns_400_invalid_token()
    {
        var (_, email) = await SignedInAsync();
        await RequestResetAsync(factory.NewClient(), email);
        var token = factory.Emails.ResetToken(email);
        await using (var db = factory.Db.Admin())
            await db.PasswordResetTokens.Where(t => db.Users.Any(u => u.Id == t.UserId && u.Email == email))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
        var client = factory.NewClient();
        await client.FetchCsrfAsync();

        var expired = await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token, new_password = NewPassword });
        var unknown = await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token = "nonsense-token", new_password = NewPassword });

        foreach (var r in new[] { expired, unknown })
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("invalid_token", (await TestClient.ReadAsync(r)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task A_weak_new_password_is_refused_and_the_link_stays_usable()
    {
        var (_, email) = await SignedInAsync();
        await RequestResetAsync(factory.NewClient(), email);
        var token = factory.Emails.ResetToken(email);
        var client = factory.NewClient();
        await client.FetchCsrfAsync();

        var weak = await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token, new_password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var problem = await TestClient.ReadAsync(weak);
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("new_password", out _));

        var ok = await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token, new_password = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
    }

    [Fact]
    public async Task Using_a_newer_link_spends_the_older_ones()
    {
        var (_, email) = await SignedInAsync();
        await RequestResetAsync(factory.NewClient(), email);
        var first = factory.Emails.ResetToken(email);
        await RequestResetAsync(factory.NewClient(), email);
        var second = factory.Emails.ResetToken(email);
        Assert.NotEqual(first, second);
        var client = factory.NewClient();
        await client.FetchCsrfAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token = second, new_password = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/auth/password-reset/confirm", new { token = first, new_password = "yet another long passphrase" })).StatusCode);
    }

    [Fact]
    public async Task Reset_requests_are_rate_limited_per_ip()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimits:ResetPerIpPerHour", "2"));
        var http = limited.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        var csrf = (await http.GetAsync("/api/v1/auth/csrf", TestContext.Current.CancellationToken)).Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("kh_csrf=", StringComparison.Ordinal)).Split(';')[0]["kh_csrf=".Length..];

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/password-reset")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { email = TestClient.UniqueEmail() }, options: TestClient.Json),
            };
            request.Headers.Add("X-CSRF-Token", csrf);
            statuses.Add((await http.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, (HttpStatusCode)429], statuses.ToArray());
    }

    // ---------- change password (FR-AUTH-07) ----------

    [Fact]
    public async Task Changing_the_password_needs_the_current_one()
    {
        var (client, _) = await SignedInAsync();

        var response = await client.SendAsync(HttpMethod.Put, "/api/v1/me/password", new { current_password = "not my password 123", new_password = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", (await TestClient.ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Changing_the_password_keeps_this_session_and_ends_all_others()
    {
        var (current, email) = await SignedInAsync();
        var other = factory.NewClient();
        await other.LoginAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/v1/me")).StatusCode); // warms the stamp cache
        var otherRefresh = other.LastRefreshCookie!;

        var changed = await current.SendAsync(HttpMethod.Put, "/api/v1/me/password", new { current_password = Password, new_password = NewPassword });

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/api/v1/me")).StatusCode); // still signed in
        Assert.Equal(HttpStatusCode.NoContent, (await current.PostAsync("/api/v1/auth/refresh")).StatusCode); // new refresh token works
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.ReplayRefreshAsync(otherRefresh)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().LoginAsync(email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient().LoginAsync(email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Changing_to_a_weak_password_returns_400_with_a_field_error_and_changes_nothing()
    {
        var (client, email) = await SignedInAsync();

        var response = await client.SendAsync(HttpMethod.Put, "/api/v1/me/password", new { current_password = Password, new_password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await TestClient.ReadAsync(response)).GetProperty("errors").TryGetProperty("new_password", out _));
        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient().LoginAsync(email, Password)).StatusCode);
    }

    [Fact]
    public async Task Password_changes_are_audited()
    {
        var (client, email) = await SignedInAsync();
        await client.SendAsync(HttpMethod.Put, "/api/v1/me/password", new { current_password = "wrong wrong wrong", new_password = NewPassword });
        await client.SendAsync(HttpMethod.Put, "/api/v1/me/password", new { current_password = Password, new_password = NewPassword });

        await using var db = factory.Db.Admin();
        var types = await db.AuditEvents.Where(e => db.Users.Any(u => u.Id == e.UserId && u.Email == email))
            .OrderBy(e => e.CreatedAt).Select(e => e.Type).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains("auth.password_change_failed", types);
        Assert.Contains("auth.password_changed", types);
    }

    // ---------- profile and preferences (FR-AUTH-08) ----------

    [Fact]
    public async Task Patch_me_updates_the_display_name_and_merges_preferences()
    {
        var (client, _) = await SignedInAsync();

        var first = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", new { display_name = "  Grace Hopper ", preferences = new { theme = "dark" } });
        var second = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", new { preferences = new { ai_auto_summary = false } });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var me = await TestClient.ReadAsync(await client.GetAsync("/api/v1/me"));
        Assert.Equal("Grace Hopper", me.GetProperty("display_name").GetString());
        var prefs = me.GetProperty("preferences");
        Assert.Equal("dark", prefs.GetProperty("theme").GetString()); // kept from the first request
        Assert.False(prefs.GetProperty("ai_auto_summary").GetBoolean());
        Assert.True(prefs.GetProperty("ai_tag_suggestions").GetBoolean()); // never touched
    }

    [Fact]
    public async Task Patch_me_with_no_fields_changes_nothing()
    {
        var (client, _) = await SignedInAsync();
        var before = await TestClient.ReadAsync(await client.GetAsync("/api/v1/me"));

        var response = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before.GetProperty("display_name").GetString(), (await TestClient.ReadAsync(response)).GetProperty("display_name").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Patch_me_rejects_a_blank_display_name(string name)
    {
        var (client, _) = await SignedInAsync();
        var response = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", new { display_name = name });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await TestClient.ReadAsync(response)).GetProperty("errors").TryGetProperty("display_name", out _));
    }

    [Fact]
    public async Task Patch_me_rejects_a_display_name_over_60_characters_and_an_unknown_theme()
    {
        var (client, _) = await SignedInAsync();

        var tooLong = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", new { display_name = new string('x', 61) });
        var badTheme = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", new { preferences = new { theme = "neon" } });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badTheme.StatusCode);
        Assert.Equal("validation_failed", (await TestClient.ReadAsync(badTheme)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_me_cannot_change_the_email_or_role()
    {
        var (client, email) = await SignedInAsync();

        await client.SendAsync(HttpMethod.Patch, "/api/v1/me", new { email = "evil@example.com", role = "admin", display_name = "Same" });

        var me = await TestClient.ReadAsync(await client.GetAsync("/api/v1/me"));
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.Equal("member", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Patch_me_needs_a_session_and_a_csrf_token()
    {
        var anonymous = factory.NewClient();
        await anonymous.FetchCsrfAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Patch, "/api/v1/me", new { display_name = "X" })).StatusCode);

        var (client, _) = await SignedInAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(HttpMethod.Patch, "/api/v1/me", new { display_name = "X" }, csrf: false)).StatusCode);
    }

    // ---------- account deletion (FR-AUTH-09) ----------

    [Fact]
    public async Task Deleting_the_account_needs_the_right_password()
    {
        var (client, email) = await SignedInAsync();

        var missing = await client.SendAsync(HttpMethod.Delete, "/api/v1/me");
        var wrong = await client.SendAsync(HttpMethod.Delete, "/api/v1/me", new { password = "not my password 123" });

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_credentials", (await TestClient.ReadAsync(wrong)).GetProperty("code").GetString());
        await using var db = factory.Db.Admin();
        Assert.True(await db.Users.AnyAsync(u => u.Email == email, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_the_account_removes_the_user_their_tokens_and_their_history_but_not_other_users()
    {
        var (client, email) = await SignedInAsync();
        var (_, bystanderEmail) = await SignedInAsync();
        await client.GetAsync("/api/v1/me");
        Guid id;
        await using (var db = factory.Db.Admin())
            id = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken);
        await RequestResetAsync(factory.NewClient(), email); // creates a reset token too

        var response = await client.SendAsync(HttpMethod.Delete, "/api/v1/me", new { password = Password });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("kh_at=;", StringComparison.Ordinal));
        await using var check = factory.Db.Admin();
        Assert.False(await check.Users.AnyAsync(u => u.Id == id, TestContext.Current.CancellationToken));
        Assert.False(await check.RefreshTokens.AnyAsync(t => t.UserId == id, TestContext.Current.CancellationToken));
        Assert.False(await check.PasswordResetTokens.AnyAsync(t => t.UserId == id, TestContext.Current.CancellationToken));
        Assert.True(await check.Users.AnyAsync(u => u.Email == bystanderEmail, TestContext.Current.CancellationToken));

        // Only the account.deleted event survives, holding the user ID and no email address.
        var events = await check.AuditEvents.Where(e => e.UserId == id).ToListAsync(TestContext.Current.CancellationToken);
        var kept = Assert.Single(events);
        Assert.Equal("account.deleted", kept.Type);
        Assert.DoesNotContain(email, kept.Data, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task After_deletion_the_old_credentials_and_the_old_session_stop_working_immediately()
    {
        var (client, email) = await SignedInAsync();
        var stolenSession = factory.NewClient();
        await stolenSession.LoginAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await stolenSession.GetAsync("/api/v1/me")).StatusCode); // warms the cache
        var refresh = stolenSession.LastRefreshCookie!;

        await client.SendAsync(HttpMethod.Delete, "/api/v1/me", new { password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().LoginAsync(email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolenSession.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolenSession.ReplayRefreshAsync(refresh)).StatusCode);
        // The email can be registered again as a brand new account.
        Assert.Equal(HttpStatusCode.Created, (await factory.NewClient().RegisterAsync(email)).StatusCode);
    }

    [Fact]
    public async Task A_failed_deletion_attempt_is_audited_without_the_password()
    {
        var (client, email) = await SignedInAsync();
        await client.SendAsync(HttpMethod.Delete, "/api/v1/me", new { password = "guess guess guess" });

        await using var db = factory.Db.Admin();
        var events = await db.AuditEvents.Where(e => db.Users.Any(u => u.Id == e.UserId && u.Email == email) && e.Type == "account.delete_failed")
            .ToListAsync(TestContext.Current.CancellationToken);

        var e = Assert.Single(events);
        Assert.DoesNotContain("guess", e.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_without_a_password_needs_a_recent_sign_in_to_be_deleted()
    {
        var (client, email) = await SignedInAsync();
        await using (var db = factory.Db.Admin())
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.PasswordHash, (string?)null).SetProperty(u => u.LastLoginAt, DateTimeOffset.UtcNow.AddMinutes(-30)), TestContext.Current.CancellationToken);

        var stale = await client.SendAsync(HttpMethod.Delete, "/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        await using (var db = factory.Db.Admin())
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, DateTimeOffset.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
        var recent = await client.SendAsync(HttpMethod.Delete, "/api/v1/me");
        Assert.Equal(HttpStatusCode.NoContent, recent.StatusCode);
    }

    [Fact]
    public void Time_checks_use_the_documented_window()
    {
        // 30 minutes for reset links and 5 minutes for the recent sign-in (Appendix C).
        var options = new KnowledgeHub.Application.Identity.AuthOptions();
        Assert.Equal(30, options.PasswordResetMinutes);
        Assert.Equal(5, options.RecentSignInMinutes);
    }
}
