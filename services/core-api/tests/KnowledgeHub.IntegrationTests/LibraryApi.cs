using System.Net;
using System.Text.Json;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

/// <summary>Short helpers over <see cref="TestClient"/> so library tests read as what they check.</summary>
public static class LibraryApi
{
    public static async Task<TestClient> UserAsync(this ApiFactory factory)
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.Created, (await client.RegisterAsync(TestClient.UniqueEmail())).StatusCode);
        return client;
    }

    public static async Task<JsonElement> Json(this HttpResponseMessage response) => await TestClient.ReadAsync(response);

    public static async Task<string> CodeAsync(this HttpResponseMessage response) =>
        (await response.Json()).GetProperty("code").GetString()!;

    public static async Task<JsonElement> ExpectAsync(this Task<HttpResponseMessage> call, HttpStatusCode status)
    {
        var response = await call;
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"expected {status}, got {(int)response.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static Task<JsonElement> CreateCollectionAsync(this TestClient c, string name, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        return c.PostAsync("/api/v1/collections", body).ExpectAsync(HttpStatusCode.Created);
    }

    /// <summary>Creates a resource. <paramref name="extra"/> properties use the API's snake_case names.</summary>
    public static Task<JsonElement> CreateResourceAsync(this TestClient c, string? url = null, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["url"] = url ?? $"https://example.com/{Guid.NewGuid():N}" };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        return c.PostAsync("/api/v1/resources", body).ExpectAsync(HttpStatusCode.Created);
    }

    public static Task<HttpResponseMessage> PatchAsync(this TestClient c, string path, object body) =>
        c.SendAsync(HttpMethod.Patch, path, body);

    public static Task<HttpResponseMessage> PutAsync(this TestClient c, string path, object body) =>
        c.SendAsync(HttpMethod.Put, path, body);

    public static Task<HttpResponseMessage> DeleteAsync(this TestClient c, string path) =>
        c.SendAsync(HttpMethod.Delete, path);

    public static string Id(this JsonElement e) => e.GetProperty("id").GetString()!;

    public static string[] Strings(this JsonElement array) => [.. array.EnumerateArray().Select(x => x.GetString()!)];

    public static string[] Ids(this JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.Id())];

    public static string[] Titles(this JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString() ?? "")];

    public static async Task<JsonElement> ListAsync(this TestClient c, string query = "") =>
        await c.GetAsync("/api/v1/resources" + (query.Length > 0 ? "?" + query : "")).ExpectAsync(HttpStatusCode.OK);

    public static string Q(string text) => Uri.EscapeDataString(text);
}
