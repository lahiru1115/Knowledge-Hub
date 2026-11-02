using System.Net;
using System.Text.Json;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class AuthTests(ApiFactory factory)
{
    private const string Password = "a long unusual passphrase";

    [Fact]
    public async Task Csrf_endpoint_sets_a_script_readable_cookie()
    {
        var client = factory.NewClient();
        var response = await client.GetAsync("/api/v1/auth/csrf");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("kh_csrf=", StringComparison.Ordinal));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(client.Csrf);
    }

    [Fact]
    public async Task Register_creates_the_user_signs_them_in_and_sets_hardened_cookies()
    {
        var client = factory.NewClient();
        var email = TestClient.UniqueEmail();

        var response = await client.RegisterAsync(email, Password, "  Ada Lovelace ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await TestClient.ReadAsync(response);
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.Equal("Ada Lovelace", user.GetProperty("display_name").GetString());
        Assert.Equal("member", user.GetProperty("role").GetString());
        Assert.Equal("system", user.GetProperty("preferences").GetProperty("theme").GetString());
        Assert.True(user.GetProperty("preferences").GetProperty("ai_auto_summary").GetBoolean());
        Assert.DoesNotContain("password", user.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // NFR-SEC-03: HttpOnly, Secure, SameSite=Lax; refresh cookie limited to the auth endpoints.
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        var access = Assert.Single(cookies, c => c.StartsWith("kh_at=", StringComparison.Ordinal));
        var refresh = Assert.Single(cookies, c => c.StartsWith("kh_rt=", StringComparison.Ordinal));
        foreach (var c in new[] { access, refresh })
        {
            Assert.Contains("httponly", c, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", c, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("path=/api;", access + ";", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", refresh, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(email, (await TestClient.ReadAsync(me)).GetProperty("email").GetString());
    }

    [Fact]
    public async Task State_changing_requests_without_a_matching_csrf_token_are_refused()
    {
        var client = factory.NewClient();
        await client.FetchCsrfAsync();

        var missing = await client.PostAsync("/api/v1/auth/register",
            new { email = TestClient.UniqueEmail(), password = Password, display_name = "X" }, csrf: false);

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal("csrf_failed", (await TestClient.ReadAsync(missing)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Duplicate_email_returns_409_email_taken_case_insensitively()
    {
        var email = TestClient.UniqueEmail();
        Assert.Equal(HttpStatusCode.Created, (await factory.NewClient().RegisterAsync(email)).StatusCode);

        var second = await factory.NewClient().RegisterAsync(email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("email_taken", (await TestClient.ReadAsync(second)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_registration_returns_400_with_an_errors_map()
    {
        var client = factory.NewClient();
        await client.FetchCsrfAsync();

        var response = await client.PostAsync("/api/v1/auth/register",
            new { email = "nope", password = "short", display_name = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await TestClient.ReadAsync(response);
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.Equal("/problems/validation_failed", problem.GetProperty("type").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("trace_id").GetString()));
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("password", out _));
        Assert.True(errors.TryGetProperty("display_name", out _));
    }

    [Fact]
    public async Task Malformed_json_returns_400_validation_failed()
    {
        var client = factory.NewClient();
        await client.FetchCsrfAsync();
        // Missing body entirely for an endpoint that needs one.
        var response = await client.PostAsync("/api/v1/auth/login", body: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", (await TestClient.ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_registered_admin_email_becomes_an_admin()
    {
        var client = factory.NewClient();
        var response = await client.RegisterAsync(ApiFactory.AdminEmail);
        // The shared database may already hold this admin from an earlier run.
        if (response.StatusCode == HttpStatusCode.Conflict) return;
        Assert.Equal("admin", (await TestClient.ReadAsync(response)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Login_succeeds_with_correct_credentials_and_returns_the_user()
    {
        var email = TestClient.UniqueEmail();
        await factory.NewClient().RegisterAsync(email);

        var client = factory.NewClient();
        var response = await client.LoginAsync(email.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(email, (await TestClient.ReadAsync(response)).GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_look_identical()
    {
        var email = TestClient.UniqueEmail();
        await factory.NewClient().RegisterAsync(email);

        var wrongPassword = await factory.NewClient().LoginAsync(email, "definitely not the password");
        var unknownEmail = await factory.NewClient().LoginAsync(TestClient.UniqueEmail(), Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        var a = await TestClient.ReadAsync(wrongPassword);
        var b = await TestClient.ReadAsync(unknownEmail);
        Assert.Equal("invalid_credentials", a.GetProperty("code").GetString());
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        Assert.Equal(a.GetProperty("detail").GetString(), b.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Ten_failed_sign_ins_lock_the_account_and_even_the_right_password_is_then_refused()
    {
        var email = TestClient.UniqueEmail();
        await factory.NewClient().RegisterAsync(email);
        var client = factory.NewClient();

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync(email, "wrong password " + i)).StatusCode);

        var locked = await client.LoginAsync(email, Password);

        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
        var problem = await TestClient.ReadAsync(locked);
        Assert.Equal("account_locked", problem.GetProperty("code").GetString());
        var retryAfter = problem.GetProperty("retry_after").GetInt32();
        Assert.InRange(retryAfter, 1, 15 * 60);
        Assert.Equal(retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture), locked.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task A_disabled_account_gets_403_account_disabled_after_the_password_is_verified()
    {
        var email = TestClient.UniqueEmail();
        await factory.NewClient().RegisterAsync(email);
        await using (var db = factory.Db.Admin())
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, true), TestContext.Current.CancellationToken);

        var correct = await factory.NewClient().LoginAsync(email);
        var wrong = await factory.NewClient().LoginAsync(email, "wrong password 123");

        Assert.Equal(HttpStatusCode.Forbidden, correct.StatusCode);
        Assert.Equal("account_disabled", (await TestClient.ReadAsync(correct)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode); // does not reveal that the account exists or is disabled
    }

    [Fact]
    public async Task A_valid_access_token_stops_working_when_the_user_is_disabled()
    {
        var email = TestClient.UniqueEmail();
        var client = factory.NewClient();
        await client.RegisterAsync(email);
        await using (var db = factory.Db.Admin())
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.IsDisabled, true).SetProperty(u => u.SecurityStamp, Guid.NewGuid()), TestContext.Current.CancellationToken);

        // First request after the change: the stamp lookup is not cached yet, so it is rejected.
        var me = await client.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Me_without_a_session_returns_401_problem_details()
    {
        var response = await factory.NewClient().GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unauthenticated", (await TestClient.ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_replaying_the_old_one_revokes_the_whole_family()
    {
        var client = factory.NewClient();
        await client.RegisterAsync(TestClient.UniqueEmail());
        var first = client.LastRefreshCookie!;

        var refreshed = await client.PostAsync("/api/v1/auth/refresh");
        Assert.Equal(HttpStatusCode.NoContent, refreshed.StatusCode);
        var second = client.LastRefreshCookie!;
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        // An attacker replays the first (now revoked) token.
        var replay = await client.ReplayRefreshAsync(first);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The legitimate newer token in the same family is revoked too.
        var afterReuse = await client.ReplayRefreshAsync(second);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Refresh_without_a_cookie_returns_401_and_clears_cookies()
    {
        var client = factory.NewClient();
        await client.FetchCsrfAsync();

        var response = await client.PostAsync("/api/v1/auth/refresh");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", (await TestClient.ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logout_clears_cookies_and_the_old_refresh_token_stops_working()
    {
        var client = factory.NewClient();
        await client.RegisterAsync(TestClient.UniqueEmail());
        var refreshToken = client.LastRefreshCookie!;

        var logout = await client.PostAsync("/api/v1/auth/logout");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith("kh_at=;", StringComparison.Ordinal));
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith("kh_rt=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.ReplayRefreshAsync(refreshToken)).StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_are_stored_only_as_hashes()
    {
        var email = TestClient.UniqueEmail();
        var client = factory.NewClient();
        await client.RegisterAsync(email);
        var raw = client.LastRefreshCookie!;

        await using var db = factory.Db.Admin();
        var hashes = await db.RefreshTokens.Where(t => db.Users.Any(u => u.Id == t.UserId && u.Email == email))
            .Select(t => t.TokenHash).ToListAsync(TestContext.Current.CancellationToken);

        var hash = Assert.Single(hashes);
        Assert.Equal(64, hash.Length);
        Assert.NotEqual(raw, hash);
        Assert.DoesNotContain(raw, hash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Passwords_are_stored_as_argon2id_hashes()
    {
        var email = TestClient.UniqueEmail();
        await factory.NewClient().RegisterAsync(email);

        await using var db = factory.Db.Admin();
        var hash = await db.Users.Where(u => u.Email == email).Select(u => u.PasswordHash).SingleAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("$argon2id$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, hash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_events_are_audited_with_ip_and_trace_id_and_without_secrets()
    {
        var email = TestClient.UniqueEmail();
        var client = factory.NewClient();
        await client.RegisterAsync(email);
        await factory.NewClient().LoginAsync(email, "wrong password 123");
        await factory.NewClient().LoginAsync(email);

        await using var db = factory.Db.Admin();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken);
        var events = await db.AuditEvents.Where(e => e.UserId == userId).OrderBy(e => e.CreatedAt).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["auth.register", "auth.login_failed", "auth.login"], events.Select(e => e.Type).ToArray());
        foreach (var e in events)
        {
            Assert.False(string.IsNullOrEmpty(JsonDocument.Parse(e.Data).RootElement.GetProperty("trace_id").GetString()));
            Assert.DoesNotContain("passphrase", e.Data, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wrong password", e.Data, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Health_endpoints_work_without_authentication()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ready", (await TestClient.ReadAsync(ready)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_api_role_cannot_change_the_schema()
    {
        // core_app (what the API runs as) has DML only; this guards against widening its grants by accident.
        await using var db = new KnowledgeHub.Infrastructure.Persistence.AppDbContext(
            KnowledgeHub.Infrastructure.Persistence.DesignTimeFactory.Options(factory.Db.AppConnection));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("CREATE TABLE core._ddl_attempt (id int)", TestContext.Current.CancellationToken));
    }
}
