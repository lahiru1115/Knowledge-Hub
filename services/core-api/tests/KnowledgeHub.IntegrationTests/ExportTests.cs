using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeHub.Application.Insights;
using KnowledgeHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace KnowledgeHub.IntegrationTests;

[Collection(ApiTestGroup.Name)]
public class ExportTests(ApiFactory factory)
{
    private static async Task<(HttpResponseMessage Response, byte[] Body)> Get(TestClient c, string format)
    {
        var response = await c.GetAsync("/api/v1/export?format=" + format);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (response, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    private static string Id(JsonElement e) => e.GetProperty("id").GetString()!;

    // ---------- JSON ----------

    [Fact]
    public async Task The_json_export_holds_collections_resources_with_every_field_and_tags_and_a_schema_version()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Databases", new { color = "teal", icon = "📘" });
        var r = await c.CreateResourceAsync("https://example.com/a", new { title = "Pooling", description = "About pooling", notes = "# My notes\nline", tags = new[] { "postgres", "perf" }, collection_id = Id(col) });
        await c.PatchAsync($"/api/v1/resources/{Id(r)}", new { summary = "A summary", is_archived = true, is_favourite = true }).ExpectAsync(HttpStatusCode.OK);

        var (response, body) = await Get(c, "json");
        var doc = JsonDocument.Parse(body).RootElement;

        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(1, doc.GetProperty("schema_version").GetInt32());
        Assert.True(doc.GetProperty("exported_at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));
        var collection = Assert.Single(doc.GetProperty("collections").EnumerateArray());
        Assert.Equal(("Databases", "teal", "📘"), (collection.GetProperty("name").GetString(), collection.GetProperty("color").GetString(), collection.GetProperty("icon").GetString()));
        var resource = Assert.Single(doc.GetProperty("resources").EnumerateArray());   // archived ones are included
        Assert.Equal(("Pooling", "# My notes\nline", "A summary", "user", true, true), (resource.GetProperty("title").GetString(), resource.GetProperty("notes").GetString(), resource.GetProperty("summary").GetString(), resource.GetProperty("summary_source").GetString(), resource.GetProperty("is_favourite").GetBoolean(), resource.GetProperty("is_archived").GetBoolean()));
        Assert.Equal(Id(col), resource.GetProperty("collection_id").GetString());
        Assert.Equal(["perf", "postgres"], resource.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["perf", "postgres"], doc.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        foreach (var field in new[] { "id", "url", "description", "type", "site_name", "image_url", "favicon_url", "metadata_status", "created_at", "updated_at" })
            Assert.True(resource.TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task Deleted_resources_and_other_users_data_are_left_out()
    {
        var c = await factory.UserAsync();
        var keep = await c.CreateResourceAsync(extra: new { title = "Keep" });
        var gone = await c.CreateResourceAsync(extra: new { title = "Gone" });
        await c.SendAsync(HttpMethod.Delete, $"/api/v1/resources/{Id(gone)}").ExpectAsync(HttpStatusCode.NoContent);
        var other = await factory.UserAsync();
        await other.CreateResourceAsync(extra: new { title = "Theirs", tags = new[] { "secret-tag" } });
        await other.CreateCollectionAsync("Their collection");

        var (_, body) = await Get(c, "json");
        var text = Encoding.UTF8.GetString(body);
        var doc = JsonDocument.Parse(body).RootElement;

        Assert.Equal([Id(keep)], doc.GetProperty("resources").EnumerateArray().Select(Id));
        Assert.Empty(doc.GetProperty("collections").EnumerateArray());
        Assert.DoesNotContain("Theirs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-tag", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Gone", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_library_still_gives_a_valid_file()
    {
        var c = await factory.UserAsync();
        var doc = JsonDocument.Parse((await Get(c, "json")).Body).RootElement;
        Assert.Empty(doc.GetProperty("resources").EnumerateArray());
        Assert.Empty(doc.GetProperty("collections").EnumerateArray());
        Assert.Empty(doc.GetProperty("tags").EnumerateArray());
        var csv = Encoding.UTF8.GetString((await Get(c, "csv")).Body);
        Assert.Equal(string.Join(',', ExportService.CsvColumns) + "\r\n", csv.TrimStart('﻿'));
    }

    [Fact]
    public async Task The_file_is_an_attachment_that_must_not_be_cached()
    {
        var c = await factory.UserAsync();
        var (json, _) = await Get(c, "json");
        var (csv, _) = await Get(c, "csv");
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("attachment", json.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal($"knowledge-hub-{today}.json", json.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Equal($"knowledge-hub-{today}.csv", csv.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", csv.Content.Headers.ContentType.CharSet);
        Assert.True(json.Headers.CacheControl!.NoStore);
    }

    // ---------- CSV ----------

    private static List<List<string>> ParseCsv(string csv)
    {
        // A small RFC 4180 reader, to check the file as a spreadsheet would read it.
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; i++; }
            else cell.Append(ch);
        }
        return rows;
    }

    [Fact]
    public async Task The_csv_has_a_utf8_byte_order_mark_a_header_and_one_row_per_resource()
    {
        var c = await factory.UserAsync();
        var col = await c.CreateCollectionAsync("Coursés");
        await c.CreateResourceAsync("https://example.com/1", new { title = "Café ✨ notes", tags = new[] { "b", "a" }, collection_id = Id(col) });
        var plain = await c.CreateResourceAsync("https://example.com/2", new { title = "Plain" });
        await c.PatchAsync($"/api/v1/resources/{Id(plain)}", new { is_favourite = true }).ExpectAsync(HttpStatusCode.OK);

        var (_, body) = await Get(c, "csv");

        Assert.Equal([0xEF, 0xBB, 0xBF], body[..3]);
        var rows = ParseCsv(Encoding.UTF8.GetString(body).TrimStart('﻿'));
        Assert.Equal(ExportService.CsvColumns, rows[0]);
        Assert.Equal(3, rows.Count);
        var first = rows[1].Zip(rows[0]).ToDictionary(p => p.Second, p => p.First);
        Assert.Equal(("Café ✨ notes", "https://example.com/1", "Coursés", "a;b"), (first["title"], first["url"], first["collection"], first["tags"]));
        var second = rows[2].Zip(rows[0]).ToDictionary(p => p.Second, p => p.First);
        Assert.Equal(("Inbox", "true", "false", ""), (second["collection"], second["favourite"], second["archived"], second["tags"]));
        Assert.All(rows, r => Assert.Equal(ExportService.CsvColumns.Length, r.Count));
    }

    [Fact]
    public async Task Commas_quotes_and_line_breaks_survive_a_round_trip()
    {
        var c = await factory.UserAsync();
        const string nasty = "He said \"hi\", then left.\nSecond line, with comma\r\nThird";
        await c.CreateResourceAsync(extra: new { title = "A, B", notes = nasty, description = "\"quoted\"" });

        var rows = ParseCsv(Encoding.UTF8.GetString((await Get(c, "csv")).Body).TrimStart('﻿'));
        var row = rows[1].Zip(rows[0]).ToDictionary(p => p.Second, p => p.First);

        Assert.Equal(2, rows.Count);
        Assert.Equal("A, B", row["title"]);
        Assert.Equal(nasty, row["notes"]);
        Assert.Equal("\"quoted\"", row["description"]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")", "'=HYPERLINK(\"http://evil\",\"x\")")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("Plain = text", "Plain = text")]
    [InlineData("", "")]
    public void A_cell_that_a_spreadsheet_would_run_as_a_formula_is_defused(string input, string expected)
    {
        // Cell() also quotes when needed, so compare what a reader gets back.
        var cell = ExportService.Cell(input);
        var read = cell.StartsWith('"') ? cell[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : cell;
        Assert.Equal(expected, read);
    }

    [Fact]
    public async Task A_title_that_looks_like_a_formula_is_defused_in_the_csv_but_exact_in_the_json()
    {
        var c = await factory.UserAsync();
        await c.CreateResourceAsync(extra: new { title = "=1+1", notes = "@home" });
        var row = ParseCsv(Encoding.UTF8.GetString((await Get(c, "csv")).Body).TrimStart('﻿'));
        var map = row[1].Zip(row[0]).ToDictionary(p => p.Second, p => p.First);
        Assert.Equal(("'=1+1", "'@home"), (map["title"], map["notes"]));
        var json = JsonDocument.Parse((await Get(c, "json")).Body).RootElement.GetProperty("resources")[0];
        Assert.Equal("=1+1", json.GetProperty("title").GetString());
    }

    // ---------- size, order, safety ----------

    [Fact]
    public async Task A_large_library_comes_out_complete_once_each_and_in_a_stable_order()
    {
        var c = await factory.UserAsync();
        var me = await c.GetAsync("/api/v1/me").ExpectAsync(HttpStatusCode.OK);
        var userId = Guid.Parse(me.GetProperty("id").GetString()!);
        const int count = ExportService.BatchSize * 2 + 137;
        var now = DateTimeOffset.UtcNow;
        await using (var db = factory.Db.Admin())
        {
            for (var i = 0; i < count; i++)
            {
                // Many share one timestamp, to prove the order does not depend on it being unique.
                var created = now.AddSeconds(-(i / 10));
                db.Resources.Add(new Resource { UserId = userId, Url = $"https://example.com/bulk/{i}", UrlNormalized = $"https://example.com/bulk/{i}", Title = $"R{i:D4}", CreatedAt = created, UpdatedAt = created });
            }
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var ids = JsonDocument.Parse((await Get(c, "json")).Body).RootElement.GetProperty("resources").EnumerateArray().Select(Id).ToList();
        var rows = ParseCsv(Encoding.UTF8.GetString((await Get(c, "csv")).Body).TrimStart('﻿'));

        Assert.Equal(count, ids.Count);
        Assert.Equal(count, ids.Distinct().Count());
        Assert.Equal(count + 1, rows.Count);
        Assert.Equal(count, rows.Skip(1).Select(r => r[1]).Distinct().Count());
        Assert.Equal(ids, JsonDocument.Parse((await Get(c, "json")).Body).RootElement.GetProperty("resources").EnumerateArray().Select(Id)); // the same order again
    }

    [Theory]
    [InlineData("")]
    [InlineData("?format=")]
    [InlineData("?format=xml")]
    [InlineData("?format=JSON%20")]
    public async Task An_unknown_format_is_a_validation_error(string query)
    {
        var c = await factory.UserAsync();
        var response = await c.GetAsync("/api/v1/export" + query);
        if (query == "?format=JSON%20") Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // case and spaces are forgiven
        else Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Exporting_needs_a_session()
    {
        await factory.NewClient().GetAsync("/api/v1/export?format=json").ExpectAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Each_export_is_recorded_in_the_audit_log_without_the_content()
    {
        var c = await factory.UserAsync();
        var me = await c.GetAsync("/api/v1/me").ExpectAsync(HttpStatusCode.OK);
        var userId = Guid.Parse(me.GetProperty("id").GetString()!);
        await c.CreateResourceAsync(extra: new { title = "Private title" });
        await Get(c, "json");
        await Get(c, "csv");

        await using var db = factory.Db.Admin();
        var events = await db.AuditEvents.AsNoTracking().Where(e => e.UserId == userId && e.Type == "data.exported").ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["csv", "json"], events.Select(e => JsonDocument.Parse(e.Data).RootElement.EnumerateObject().Single(p => string.Equals(p.Name, "format", StringComparison.OrdinalIgnoreCase)).Value.GetString()!).Order());
        Assert.DoesNotContain(events, e => e.Data.Contains("Private title", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exports_are_rate_limited_per_user()
    {
        using var web = factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimits:ExportPerUserPerMinute", "2"));
        var c = new TestClient(web);
        await c.RegisterAsync(TestClient.UniqueEmail());
        await Get(c, "json");
        await Get(c, "csv");
        var limited = await c.GetAsync("/api/v1/export?format=json");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", (await limited.Json()).GetProperty("code").GetString());
        var other = new TestClient(web);
        await other.RegisterAsync(TestClient.UniqueEmail());
        await Get(other, "json");   // someone else is not affected
    }
}
