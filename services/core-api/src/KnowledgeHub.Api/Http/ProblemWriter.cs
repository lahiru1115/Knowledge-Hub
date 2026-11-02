using System.Diagnostics;
using System.Text.Json;
using KnowledgeHub.Application.Common;

namespace KnowledgeHub.Api.Http;

/// <summary>Writes RFC 9457 Problem Details with the fields from IR-12 / Doc 02 §8.4.</summary>
public static class ProblemWriter
{
    public static async Task WriteAsync(
        HttpContext http,
        ProblemCode code,
        string? detail = null,
        IDictionary<string, string[]>? errors = null,
        IDictionary<string, object?>? extensions = null)
    {
        var codeText = ProblemCatalogue.Code(code);
        var status = ProblemCatalogue.Status(code);
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"/problems/{codeText}",
            ["title"] = ProblemCatalogue.Title(code),
            ["status"] = status,
            ["detail"] = detail ?? ProblemCatalogue.Title(code),
            ["instance"] = http.Request.Path.Value,
            ["code"] = codeText,
            ["trace_id"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
        };
        if (errors is { Count: > 0 }) body["errors"] = errors;
        if (extensions is not null)
            foreach (var (k, v) in extensions) body[k] = v;

        if (http.Response.HasStarted) return;
        http.Response.Clear();
        http.Response.StatusCode = status;
        if (extensions?.TryGetValue("retry_after", out var ra) == true && ra is int seconds)
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(http.Response.Body, body, ApiJson.Value, http.RequestAborted);
    }
}

public static class ApiJson
{
    public static readonly JsonSerializerOptions Value = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
        };
        o.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return o;
    }
}
