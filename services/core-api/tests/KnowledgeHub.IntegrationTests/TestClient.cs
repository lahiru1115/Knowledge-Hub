using Xunit;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace KnowledgeHub.IntegrationTests;

/// <summary>
/// A browser-like client: keeps cookies (honouring Secure and Path, so the cookie design is really exercised)
/// and sends the CSRF header the way the SPA does.
/// </summary>
public sealed class TestClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly WebApplicationFactory<Program> _factory;
    public string? Csrf { get; private set; }
    public string? LastRefreshCookie { get; private set; }

    public TestClient(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
    }

    /// <summary>Headers added to every request, for example X-Forwarded-For.</summary>
    public Dictionary<string, string> DefaultHeaders { get; } = [];

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, path);
        foreach (var (k, v) in DefaultHeaders) request.Headers.TryAddWithoutValidation(k, v);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (csrf && Csrf is not null) request.Headers.Add("X-CSRF-Token", Csrf);
        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        Capture(response);
        return response;
    }

    /// <summary>Sends exactly this JSON text, for bodies that serialisation would change.</summary>
    public async Task<HttpResponseMessage> SendRawJsonAsync(HttpMethod method, string path, string json)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        if (Csrf is not null) request.Headers.Add("X-CSRF-Token", Csrf);
        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        Capture(response);
        return response;
    }

    /// <summary>
    /// Sends POST /auth/refresh with a specific (possibly old) refresh token, bypassing the cookie jar,
    /// the way an attacker replaying a stolen cookie would.
    /// </summary>
    public async Task<HttpResponseMessage> ReplayRefreshAsync(string refreshToken)
    {
        using var plain = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", $"kh_rt={refreshToken}; kh_csrf={Csrf}");
        request.Headers.Add("X-CSRF-Token", Csrf);
        return await plain.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, bool csrf = true) =>
        SendAsync(HttpMethod.Post, path, body, csrf);

    /// <summary>GET /auth/csrf, as the SPA does before any sign-in.</summary>
    public async Task FetchCsrfAsync()
    {
        var response = await GetAsync("/api/v1/auth/csrf");
        response.EnsureSuccessStatusCode();
    }

    public async Task<HttpResponseMessage> RegisterAsync(string email, string password = "a long unusual passphrase", string name = "Test User")
    {
        await FetchCsrfAsync();
        return await PostAsync("/api/v1/auth/register", new { email, password, display_name = name });
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password = "a long unusual passphrase")
    {
        if (Csrf is null) await FetchCsrfAsync();
        return await PostAsync("/api/v1/auth/login", new { email, password });
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);

    public static string UniqueEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private void Capture(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return;
        foreach (var c in cookies)
        {
            var pair = c.Split(';')[0];
            if (pair.StartsWith("kh_csrf=", StringComparison.Ordinal)) Csrf = pair["kh_csrf=".Length..];
            if (pair.StartsWith("kh_rt=", StringComparison.Ordinal)) LastRefreshCookie = pair["kh_rt=".Length..];
        }
    }
}
