using System.Text;
using System.Text.Json;
using KnowledgeHub.Application.Common;
using KnowledgeHub.Application.Identity;
using KnowledgeHub.Application.Library;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeHub.Application.Insights;

/// <summary>
/// Writes a user's library to a stream as JSON or CSV (FR-EXP-01, FR-EXP-02). It reads in batches and writes as it goes,
/// so a large library never has to fit in memory. Deleted resources are left out; archived ones are included.
/// </summary>
public sealed class ExportService(IAppDbContext db, IClock clock, AuditLog audit)
{
    public const int SchemaVersion = 1;
    public const int BatchSize = 500;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string FileName(string format, DateTimeOffset now) => $"knowledge-hub-{now:yyyy-MM-dd}.{format}";

    /// <summary>Records that an export was made (NFR-SEC-13); call before streaming, as headers go out first.</summary>
    public async Task RecordAsync(Guid userId, string format, CancellationToken ct)
    {
        audit.Write("data.exported", userId, new { format });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Resources in a stable order, one batch at a time, with their tags.</summary>
    private async IAsyncEnumerable<IReadOnlyList<ResourceDto>> BatchesAsync(Guid userId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        DateTimeOffset? afterCreated = null;
        Guid afterId = Guid.Empty;
        while (true)
        {
            var query = db.Resources.OwnedBy(userId).AsNoTracking();
            if (afterCreated is { } created)
            {
                var id = afterId;
                query = query.Where(r => r.CreatedAt > created || (r.CreatedAt == created && r.Id.CompareTo(id) > 0));
            }
            var batch = await query.Include(r => r.ResourceTags).ThenInclude(rt => rt.Tag)
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) yield break;
            yield return [.. batch.Select(ResourceService.ToDto)];
            (afterCreated, afterId) = (batch[^1].CreatedAt, batch[^1].Id);
            if (batch.Count < BatchSize) yield break;
        }
    }

    // ---------- JSON ----------

    public async Task WriteJsonAsync(Guid userId, Stream output, CancellationToken ct)
    {
        // Written to a buffer and drained with WriteAsync: the serializer flushes synchronously, which a response
        // stream does not allow.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        await using var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
        async Task Drain()
        {
            w.Flush();
            await output.WriteAsync(buffer.WrittenMemory, ct);
            buffer.Clear();
        }

        w.WriteStartObject();
        w.WriteNumber("schema_version", SchemaVersion);
        w.WriteString("exported_at", clock.UtcNow);

        w.WriteStartArray("collections");
        var collections = await db.Collections.OwnedBy(userId).AsNoTracking()
            .OrderBy(c => c.Position).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        foreach (var c in collections)
            JsonSerializer.Serialize(w, new
            {
                c.Id,
                c.Name,
                c.Description,
                c.Color,
                c.Icon,
                c.IsPinned,
                c.Position,
                c.CreatedAt,
                c.UpdatedAt,
            }, s_json);
        w.WriteEndArray();

        w.WriteStartArray("resources");
        await foreach (var batch in BatchesAsync(userId, ct))
        {
            foreach (var r in batch) JsonSerializer.Serialize(w, r, s_json);
            await Drain();
        }
        w.WriteEndArray();

        w.WriteStartArray("tags");
        var tags = await db.Tags.OwnedBy(userId).AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct);
        foreach (var t in tags) w.WriteStringValue(t);
        w.WriteEndArray();

        w.WriteEndObject();
        await Drain();
    }

    // ---------- CSV ----------

    public static readonly string[] CsvColumns =
    [
        "title", "url", "type", "collection", "tags", "favourite", "archived", "created_at", "updated_at",
        "site_name", "description", "summary", "summary_source", "notes",
    ];

    public async Task WriteCsvAsync(Guid userId, Stream output, CancellationToken ct)
    {
        var names = await db.Collections.OwnedBy(userId).AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        // A byte order mark makes Excel read the file as UTF-8 instead of guessing a legacy code page.
        await using var w = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true) { NewLine = "\r\n" };
        await w.WriteLineAsync(string.Join(',', CsvColumns));
        await foreach (var batch in BatchesAsync(userId, ct))
        {
            foreach (var r in batch)
            {
                string?[] cells =
                [
                    r.Title, r.Url, Snake(r.Type), r.CollectionId is { } id && names.TryGetValue(id, out var n) ? n : "Inbox",
                    string.Join(';', r.Tags), r.IsFavourite ? "true" : "false", r.IsArchived ? "true" : "false",
                    r.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    r.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    r.SiteName, r.Description, r.Summary, Snake(r.SummarySource), r.Notes,
                ];
                await w.WriteLineAsync(string.Join(',', cells.Select(Cell)));
            }
            await w.FlushAsync(ct);
        }
    }

    private static string Snake<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>
    /// One CSV cell. Quoted when it holds a comma, quote or line break. A cell that starts with =, +, - or @ (or a tab or
    /// carriage return) would be run as a formula by a spreadsheet, so it gets a leading apostrophe (OWASP "CSV injection").
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
