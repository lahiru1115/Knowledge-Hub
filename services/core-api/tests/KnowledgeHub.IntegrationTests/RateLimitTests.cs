using System.Net;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class RateLimitTests(ApiFactory factory)
{
    [Fact]
    public async Task Sixth_sign_in_attempt_within_a_minute_for_the_same_email_and_ip_is_rate_limited()
    {
        using var limited = factory.WithWebHostBuilder(b =>
            b.UseSetting("Auth:RateLimits:LoginPerIpAndEmailPerMinute", "5"));
        var http = limited.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        var token = await GetCsrfAsync(http);
        var email = TestClient.UniqueEmail();

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            last = await LoginAsync(http, token, email);
            statuses.Add(last.StatusCode);
        }

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.Unauthorized, 5), (HttpStatusCode)429], statuses.ToArray());
        Assert.True(last!.Headers.Contains("Retry-After"));
        Assert.Equal("rate_limited", (await TestClient.ReadAsync(last)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Registrations_beyond_the_hourly_limit_per_ip_are_rate_limited()
    {
        using var limited = factory.WithWebHostBuilder(b =>
            b.UseSetting("Auth:RateLimits:RegisterPerIpPerHour", "2"));
        var http = limited.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        var token = await GetCsrfAsync(http);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
            {
                Content = System.Net.Http.Json.JsonContent.Create(
                    new { email = TestClient.UniqueEmail(), password = "a long unusual passphrase", display_name = "R" }, options: TestClient.Json),
            };
            request.Headers.Add("X-CSRF-Token", token);
            var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
            // Registering rotates the CSRF cookie, exactly as sign-in does; the SPA re-reads it each time.
            if (response.Headers.TryGetValues("Set-Cookie", out var set) &&
                set.FirstOrDefault(c => c.StartsWith("kh_csrf=", StringComparison.Ordinal)) is { } csrf)
                token = csrf.Split(';')[0]["kh_csrf=".Length..];
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, (HttpStatusCode)429], statuses.ToArray());
    }

    private static async Task<string> GetCsrfAsync(HttpClient http)
    {
        var response = await http.GetAsync("/api/v1/auth/csrf", TestContext.Current.CancellationToken);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("kh_csrf=", StringComparison.Ordinal));
        return cookie.Split(';')[0]["kh_csrf=".Length..];
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient http, string csrf, string email)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { email, password = "wrong password 123" }, options: TestClient.Json),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
